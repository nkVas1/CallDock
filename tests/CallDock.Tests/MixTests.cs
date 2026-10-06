using CallDock.Core;

namespace CallDock.Tests;

public sealed class MixTests
{
    private static RecordingTrack Track(SourceKind kind, string name, double offset = 0, bool video = false, bool audio = true, double end = 0) =>
        new() { Name = name, Kind = kind, OffsetSeconds = offset, EndSeconds = end, HasVideo = video, HasAudio = audio };

    private static MixPlan? Automatic(params RecordingTrack[] tracks) => MixPlan.Automatic(new CallSession { Tracks = [.. tracks] }, _ => true);

    [Fact]
    public void ACallBecomesOneSound()
    {
        var plan = Automatic(Track(SourceKind.Microphone, "Я"), Track(SourceKind.SystemAudio, "Собеседники"));
        Assert.NotNull(plan);
        Assert.Null(plan.Video);
        Assert.Equal(2, plan.Audio.Count);
    }

    [Fact]
    public void TheScreenGetsEveryonesSound()
    {
        var screen = Track(SourceKind.Screen, "Экран", 0.4, video: true, audio: false);
        var plan = Automatic(Track(SourceKind.Microphone, "Я"), screen, Track(SourceKind.SystemAudio, "Собеседники"));
        Assert.Same(screen, plan?.Video);
        Assert.Equal(2, plan!.Audio.Count);
    }

    [Fact]
    public void OneTrackNeedsNoMix() => Assert.Null(Automatic(Track(SourceKind.Microphone, "Я")));

    [Fact]
    public void ATabAloneAlreadyHasItsSound() => Assert.Null(Automatic(Track(SourceKind.BrowserTab, "Зал 1", video: true)));

    [Fact]
    public void ASilentScreenAloneNeedsNoMix() => Assert.Null(Automatic(Track(SourceKind.Screen, "Экран", video: true, audio: false)));

    [Fact]
    public void AMeetingInATabGetsTheMicrophone()
    {
        var tab = Track(SourceKind.BrowserTab, "Встреча", video: true);
        var plan = Automatic(tab, Track(SourceKind.Microphone, "Я", 3));
        Assert.Same(tab, plan?.Video);
        Assert.Equal(2, plan!.Audio.Count);
    }

    [Fact]
    public void ConferenceHallsAreNotMixedTogether() => Assert.Null(Automatic(
        Track(SourceKind.BrowserTab, "Зал 1", video: true), Track(SourceKind.BrowserTab, "Зал 2", video: true), Track(SourceKind.Microphone, "Я")));

    [Fact]
    public void TheScreenIsThePictureEvenWhenATabStartedFirst()
    {
        var screen = Track(SourceKind.Screen, "Экран", 2, video: true, audio: false);
        Assert.Same(screen, Automatic(Track(SourceKind.BrowserTab, "Встреча", 0, video: true), screen)?.Video);
    }

    [Fact]
    public void AScreenAddedLateLeavesTheWholeConversationAsSound()
    {
        var plan = Automatic(Track(SourceKind.Microphone, "Я", 0, end: 600), Track(SourceKind.SystemAudio, "Собеседники", 0.3, end: 600),
            Track(SourceKind.Screen, "Экран", 240, video: true, audio: false, end: 600));
        Assert.Null(plan?.Video);
        Assert.Equal(2, plan!.Audio.Count);
    }

    [Fact]
    public void TracksArePlacedWhereTheyStarted()
    {
        var args = Mixer.Arguments(new MixInput("video.txt", 0, 60), copyVideo: true,
            [new("late.txt", 1.25, 50), new("early.txt", -0.5, 60), new("same.txt", 0, 60)], "mix.mp4").ToList();
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[1:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=1250:all=1[a0]", filter);
        Assert.Contains("[2:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=start=0.5,asetpts=PTS-STARTPTS[a1]", filter);
        Assert.Contains("[3:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,anull[a2]", filter);
        Assert.EndsWith("amix=inputs=3:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.97:level=false,apad[mix]", filter);
        Assert.Equal(["-map", "0:v:0", "-c:v", "copy"], args.SkipWhile(a => a != "-map").Take(4));
        Assert.Contains("-shortest", args);
        Assert.Equal("mix.mp4", args[^1]);
    }

    [Fact]
    public void SoundAloneIsNeitherPaddedNorCut()
    {
        var args = Mixer.Arguments(null, copyVideo: false, [new("a.txt", 0, 10), new("b.txt", 2, 10)], "mix.m4a").ToList();
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.StartsWith("[0:a]", filter);
        Assert.DoesNotContain("apad", filter);
        Assert.DoesNotContain("-shortest", args);
        Assert.DoesNotContain("0:v:0", args);
        Assert.Contains("aac", args);
    }

    [Fact]
    public void APictureFromChromeIsConvertedToH264() =>
        Assert.Contains("h264_mf", Mixer.Arguments(new MixInput("tab.txt", 0, 30), copyVideo: false, [new("tab.txt", 0, 30)], "mix.mp4"));

    [Theory]
    [InlineData("all.mp3", "libmp3lame")]
    [InlineData("all.flac", "flac")]
    [InlineData("all.wav", "pcm_f32le")]
    public void ExportsKeepTheirFormat(string output, string codec) =>
        Assert.Contains(codec, Mixer.Arguments(null, false, [new("a.txt", 0, 10), new("b.txt", 0, 10)], output));
}
