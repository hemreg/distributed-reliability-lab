namespace Dependency;

/// <summary>How one <c>GET /work</c> request ended. Everything except <see cref="Ok"/> is answered 503.</summary>
public enum WorkOutcome
{
    Ok,

    /// <summary>E03-A: injected transient failure after doing the work.</summary>
    Injected503,

    /// <summary>Workers and queue full: rejected at once, no work done (load shedding).</summary>
    Rejected503,

    /// <summary>E03-B: the request was inside the process when it restarted.</summary>
    Restart503,

    /// <summary>E03-B: arrived during the outage after the restart (nothing is admitted).</summary>
    Refused503,
}

/// <summary>
/// The simulated work behind <c>GET /work</c>, without HTTP, so the tests can drive it directly.
/// </summary>
public static class WorkProcessor
{
    public static Task<WorkOutcome> ProcessAsync(ServerRun run, DependencyOptions options, CancellationToken clientAborted) =>
        run.Mode == FailureMode.CorrelatedRestart
            ? CorrelatedAsync(run, options, clientAborted)
            : WindowAsync(run, options, clientAborted);

    /// <summary>E03-A (and warm-up): bounded queue, failure window with slower service and injected 503s.</summary>
    private static async Task<WorkOutcome> WindowAsync(ServerRun run, DependencyOptions options, CancellationToken clientAborted)
    {
        var model = run.Model;
        var arrived = run.ElapsedMs;
        run.Received(arrived);

        if (!model.TryAdmit(out var inSystem))
        {
            run.Rejected(arrived);
            run.Observe(arrived, inSystem, Math.Max(0, inSystem - options.Workers));
            return WorkOutcome.Rejected503;
        }
        run.Observe(arrived, inSystem, Math.Max(0, inSystem - options.Workers));

        try
        {
            await model.WaitForWorkerAsync();
            try
            {
                // Whether the request is degraded is decided when its service starts.
                var degraded = run.InFailureWindow(options);
                // Simulated work. Deliberately NOT cancelled when the client gives up: like many real
                // servers, the dependency finishes work nobody is waiting for any more ("abandoned").
                await Task.Delay(degraded ? options.FailureServiceTimeMs : options.ServiceTimeMs);
                var fail = degraded && run.NextDouble() < options.FailureErrorRate;

                var done = run.ElapsedMs;
                if (clientAborted.IsCancellationRequested) run.Abandoned(done);
                if (fail)
                {
                    run.Injected(done);
                    return WorkOutcome.Injected503;
                }
                run.Ok(done);
                return WorkOutcome.Ok;
            }
            finally
            {
                model.ReleaseWorker();
            }
        }
        finally
        {
            var before = model.Leave();
            run.Observe(run.ElapsedMs, before, Math.Max(0, before - options.Workers));
        }
    }

    /// <summary>
    /// E03-B. Before the restart: normal service, except that from the freeze on nothing completes and
    /// nothing is rejected; every request inside is held until the restart fails it (503). After the
    /// restart: a fresh process, optionally refusing everything during the outage, then cold (slower)
    /// service, then normal service. The bounded queue applies throughout except during the freeze.
    /// </summary>
    private static async Task<WorkOutcome> CorrelatedAsync(ServerRun run, DependencyOptions options, CancellationToken clientAborted)
    {
        var failure = run.Correlated;
        var arrived = run.ElapsedMs;
        run.Received(arrived);

        if (!run.HasRestarted)
            return await BeforeRestartAsync(run, options, failure, arrived, clientAborted);

        if (failure.PhaseAt(arrived) == CorrelatedPhase.Outage)
        {
            run.Refused(arrived);
            return WorkOutcome.Refused503;
        }

        var model = run.ModelAfterRestart;
        if (!model.TryAdmit(out var inSystem))
        {
            run.Rejected(arrived);
            Observe(run, options, arrived, model, inSystem);
            return WorkOutcome.Rejected503;
        }
        Observe(run, options, arrived, model, inSystem);

        try
        {
            await model.WaitForWorkerAsync();
            try
            {
                // Cold start or normal, decided when the service starts (as in E03-A). Not cancelled
                // when the client gives up.
                await Task.Delay(failure.ServiceTimeAt(run.ElapsedMs));
                var done = run.ElapsedMs;
                if (clientAborted.IsCancellationRequested) run.Abandoned(done);
                run.Ok(done);
                return WorkOutcome.Ok;
            }
            finally
            {
                model.ReleaseWorker();
            }
        }
        finally
        {
            var before = model.Leave();
            Observe(run, options, run.ElapsedMs, model, before);
        }
    }

    private static async Task<WorkOutcome> BeforeRestartAsync(
        ServerRun run, DependencyOptions options, CorrelatedFailure failure, double arrived, CancellationToken clientAborted)
    {
        var model = run.Model;
        var restart = run.RestartToken;
        var frozen = arrived >= failure.FreezeStartMs;

        int inSystem;
        if (frozen)
        {
            inSystem = model.AdmitUnbounded();
        }
        else if (!model.TryAdmit(out inSystem))
        {
            run.Rejected(arrived);
            Observe(run, options, arrived, model, inSystem);
            return WorkOutcome.Rejected503;
        }
        Observe(run, options, arrived, model, inSystem);

        try
        {
            // Arrived during the freeze: the hung process never gets to it.
            if (frozen) await Task.Delay(Timeout.Infinite, restart);

            await model.WaitForWorkerOrRestartAsync(restart);
            try
            {
                await Task.Delay(options.ServiceTimeMs, restart);
                // Finished its work after the freeze began: the response is never sent; held until the restart.
                if (run.ElapsedMs >= failure.FreezeStartMs) await Task.Delay(Timeout.Infinite, restart);

                var done = run.ElapsedMs;
                if (clientAborted.IsCancellationRequested) run.Abandoned(done);
                run.Ok(done);
                return WorkOutcome.Ok;
            }
            finally
            {
                model.ReleaseWorker();
            }
        }
        catch (OperationCanceledException) when (restart.IsCancellationRequested)
        {
            run.RestartFailed(run.ElapsedMs);
            return WorkOutcome.Restart503;
        }
        finally
        {
            var before = model.Leave();
            Observe(run, options, run.ElapsedMs, model, before);
        }
    }

    // In-system across both processes for the time series; queued within this request's process.
    private static void Observe(ServerRun run, DependencyOptions options, double ms, CapacityModel model, int ownCount)
    {
        var other = ReferenceEquals(model, run.Model) ? run.ModelAfterRestart : run.Model;
        var total = ReferenceEquals(other, model) ? ownCount : ownCount + other.InSystem;
        run.Observe(ms, total, Math.Max(0, ownCount - options.Workers));
        if (ReferenceEquals(model, run.ModelAfterRestart) && !ReferenceEquals(run.Model, run.ModelAfterRestart))
            run.ObserveRestarted(ownCount);
    }
}
