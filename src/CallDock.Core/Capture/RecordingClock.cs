using System.Diagnostics;

namespace CallDock.Core;

/// <summary>
/// The time of a recording: how long it has recorded, pauses left out. Every track is placed on this timeline and every
/// source leaves the same pauses out, so the tracks stay in step however often the recording is paused. Moments are
/// <see cref="Stopwatch"/> timestamps. The recorder changes the clock under its lock; any thread may read it.
/// </summary>
public sealed class RecordingClock(long startedAt)
{
    /// <summary>A pause from one moment to another; <see cref="Open"/> while it lasts.</summary>
    private readonly record struct Gap(long From, long To);
    private const long Open = long.MaxValue;
    /// <summary>Replaced as a whole on every change, so a reader on another thread always sees a consistent list.</summary>
    private volatile Gap[] gaps = [];

    public static RecordingClock StartNew() => new(Stopwatch.GetTimestamp());

    /// <summary>When the recording started.</summary>
    public long StartedAt { get; } = startedAt;
    public bool IsPaused => gaps is [.., { To: Open }];
    /// <summary>Seconds recorded until now; it stands still during a pause.</summary>
    public double Elapsed => SecondsAt(Stopwatch.GetTimestamp());

    /// <summary>Stops the time of the recording at <paramref name="at"/>.</summary>
    public void Pause(long at)
    {
        var current = gaps;
        if (current is [.., { To: Open }]) return;
        var from = current is [.., var last] ? Math.Max(at, last.To) : Math.Max(at, StartedAt);
        gaps = [.. current, new Gap(from, Open)];
    }

    /// <summary>The time of the recording goes on from <paramref name="at"/>.</summary>
    public void Resume(long at)
    {
        var current = gaps;
        if (current is not [.., { To: Open } last]) return;
        var next = current.ToArray();
        next[^1] = last with { To = Math.Max(at, last.From) };
        gaps = next;
    }

    /// <summary>Seconds recorded by a moment. A moment within a pause counts as the moment the pause began; one before
    /// the start counts as the start.</summary>
    public double SecondsAt(long timestamp)
    {
        var ticks = timestamp - StartedAt;
        foreach (var gap in gaps)
        {
            if (gap.From >= timestamp) break;
            ticks -= Math.Min(timestamp, gap.To) - gap.From;
        }
        return Math.Max(0, ticks) / (double)Stopwatch.Frequency;
    }

    /// <summary>The moment falls within a pause: what was heard or seen then is not recorded.</summary>
    public bool IsPausedAt(long timestamp)
    {
        foreach (var gap in gaps)
            if (gap.From <= timestamp && timestamp < gap.To) return true;
        return false;
    }

    /// <summary>A moment as Windows audio stamps it — the performance counter in 100-nanosecond units — as a timestamp.</summary>
    public static long FromHundredNanoseconds(long value) =>
        Stopwatch.Frequency == 10_000_000 ? value : (long)(value * (Stopwatch.Frequency / 10_000_000.0));
}
