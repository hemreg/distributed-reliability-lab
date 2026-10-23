namespace LoadGenerator;

public static class ArrivalSchedule
{
    /// <summary>
    /// Open-loop Poisson arrivals: exponential gaps with mean 1/rate, from a seeded Random, so every
    /// strategy gets exactly the same schedule. Returns start offsets in milliseconds, ascending,
    /// all in [0, duration).
    /// </summary>
    public static double[] Poisson(double ratePerSecond, double durationSeconds, int seed)
    {
        var random = new Random(seed);
        var arrivals = new List<double>((int)(ratePerSecond * durationSeconds * 1.1) + 16);
        var t = 0.0;
        while (true)
        {
            t += -Math.Log(1.0 - random.NextDouble()) / ratePerSecond;
            if (t >= durationSeconds) break;
            arrivals.Add(t * 1000.0);
        }
        return arrivals.ToArray();
    }
}
