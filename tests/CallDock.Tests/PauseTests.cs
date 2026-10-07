using CallDock.Core;
using System.Diagnostics;

namespace CallDock.Tests;

public sealed class PauseTests
{
    private static long At(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void APauseIsLeftOutOfTheTimeline()
    {
        var clock = new RecordingClock(At(0));
        clock.Pause(At(4));
        clock.Resume(At(8));
        Assert.Equal(3, clock.SecondsAt(At(3)), 6);
        Assert.Equal(4, clock.SecondsAt(At(5)), 6);   // within the pause: where it began
        Assert.Equal(6, clock.SecondsAt(At(10)), 6);
        Assert.False(clock.IsPausedAt(At(3.999)));
        Assert.True(clock.IsPausedAt(At(4)));
        Assert.True(clock.IsPausedAt(At(7.999)));
        Assert.False(clock.IsPausedAt(At(8)));         // the moment of resuming is recorded again
        Assert.False(clock.IsPaused);
    }

    [Fact]
    public void PausesAddUpAndAnOpenOneHoldsTheTime()
    {
        var clock = new RecordingClock(At(10));
        clock.Pause(At(12));
        clock.Resume(At(13));
        clock.Pause(At(15));
        clock.Resume(At(17));
        Assert.Equal(6, clock.SecondsAt(At(19)), 6);
        clock.Pause(At(20));
        Assert.True(clock.IsPaused);
        Assert.Equal(7, clock.SecondsAt(At(100)), 6);
        Assert.Equal(0, clock.SecondsAt(At(5)), 6);   // before the start
    }

    [Fact]
    public void RepeatedCommandsChangeNothing()
    {
        var clock = new RecordingClock(At(0));
        clock.Resume(At(1));                 // nothing to resume
        clock.Pause(At(2));
        clock.Pause(At(3));                  // already paused: the pause began at 2
        clock.Resume(At(4));
        clock.Resume(At(5));
        Assert.Equal(4, clock.SecondsAt(At(6)), 6);
    }

    [Theory]
    [InlineData(40, "40 с")]
    [InlineData(89, "1 мин")]
    [InlineData(299, "5 мин")]
    [InlineData(3600, "1 ч")]
    [InlineData(4320, "1 ч 12 мин")]
    public void PauseLengthsReadAsPeopleSayThem(double seconds, string text) => Assert.Equal(text, Display.Span(seconds));
}
