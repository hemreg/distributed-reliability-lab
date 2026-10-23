namespace Dependency;

/// <summary>
/// Finite capacity: <c>workers</c> requests are served at a time, up to <c>queueLimit</c> more wait
/// in FIFO order, and anything beyond that is rejected at once (load shedding, answered with 503).
/// </summary>
/// <remarks>
/// Service time is simulated with a timer, not with CPU work, so capacity does not depend on the
/// machine the lab runs on: <c>workers / service time</c> requests per second, exactly.
/// </remarks>
public sealed class CapacityModel(int workers, int queueLimit)
{
    private readonly SemaphoreSlim _workers = new(workers, workers); // async waiters are released in FIFO order
    private int _inSystem;

    public int Workers => workers;
    public int QueueLimit => queueLimit;

    /// <summary>Requests inside the dependency right now: in service plus waiting in the queue.</summary>
    public int InSystem => Volatile.Read(ref _inSystem);

    /// <summary>
    /// Admits the request if fewer than <c>workers + queueLimit</c> are inside; otherwise rejects it.
    /// <paramref name="inSystem"/> is the count including this request (or the full count on rejection).
    /// Every admitted request must call <see cref="Leave"/> exactly once.
    /// </summary>
    public bool TryAdmit(out int inSystem)
    {
        var n = Interlocked.Increment(ref _inSystem);
        if (n > workers + queueLimit)
        {
            inSystem = Interlocked.Decrement(ref _inSystem);
            return false;
        }
        inSystem = n;
        return true;
    }

    /// <summary>
    /// Admits without the limit (E03-B freeze: a hung process does not run its admission check, the
    /// connections just pile up). Returns the count including this request; call <see cref="Leave"/> once.
    /// </summary>
    public int AdmitUnbounded() => Interlocked.Increment(ref _inSystem);

    public Task WaitForWorkerAsync() => _workers.WaitAsync();

    /// <summary>E03-B: waits for a worker unless the restart (<paramref name="restart"/>) comes first.</summary>
    public Task WaitForWorkerOrRestartAsync(CancellationToken restart) => _workers.WaitAsync(restart);

    public void ReleaseWorker() => _workers.Release();

    /// <summary>Returns the in-system count just before this request left.</summary>
    public int Leave() => Interlocked.Decrement(ref _inSystem) + 1;
}
