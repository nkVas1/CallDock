namespace CallDock.Core;

/// <summary>
/// The loudest sample since the meter was last read. Audio arrives every ~10 ms, the window redraws every 100 ms:
/// showing only the last packet makes the meter flicker (and an empty packet would show silence).
/// Thread-safe: the audio thread feeds it, the UI thread takes it.
/// </summary>
public sealed class PeakMeter
{
    private float peak;
    private long lastFeed;

    public void Feed(float value)
    {
        if (!float.IsFinite(value)) return;
        float current;
        do
        {
            current = Volatile.Read(ref peak);
            if (value <= current) break;
        }
        while (Interlocked.CompareExchange(ref peak, value, current) != current);
        Interlocked.Exchange(ref lastFeed, Environment.TickCount64);
    }

    /// <summary>The peak since the previous call (0…1), or 0 when no sound arrived for a while.</summary>
    public float Take()
    {
        var value = Interlocked.Exchange(ref peak, 0f);
        return Environment.TickCount64 - Interlocked.Read(ref lastFeed) < 500 ? Math.Min(1, value) : 0;
    }
}
