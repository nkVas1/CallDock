using System.Globalization;

namespace CallDock.Core;

/// <summary>What goes into a mix: the sound of these tracks, over this picture if there is one.</summary>
public sealed record MixPlan(IReadOnlyList<RecordingTrack> Audio, RecordingTrack? Video)
{
    /// <summary>
    /// The mix made after a recording: everyone's sound over the screen video (or over a tab or a stream when nothing else
    /// shows a picture), or the sound alone. Null when there is nothing to mix — a single track, a tab that already has its
    /// own sound — and when the recording holds several broadcasts: the halls of a conference must not be heard at once.
    /// </summary>
    public static MixPlan? Automatic(CallSession session, Func<RecordingTrack, bool> hasData)
    {
        var tracks = session.Tracks.Where(hasData).ToArray();
        var audio = tracks.Where(t => t.HasAudio).ToArray();
        if (audio.Count(IsBroadcast) >= 2) return null;
        var video = Picture(tracks);
        // A picture that joined late or left early would cut the conversation down to its own length: the sound alone, whole.
        if (video is not null && !Spans(video, audio)) video = null;
        if (video is null) return audio.Length >= 2 ? new(audio, null) : null;
        return audio.All(t => t.Id == video.Id) ? null : new(audio, video);
    }

    /// <summary>A screen capture takes a few seconds to start on a slow computer: a picture within this distance of the first
    /// and last voice still spans the conversation.</summary>
    private const double SpanTolerance = 10;

    /// <summary>The picture started with the first voice and ended with the last, give or take a few seconds.</summary>
    private static bool Spans(RecordingTrack video, IReadOnlyList<RecordingTrack> audio)
    {
        if (audio.Count == 0) return true;
        var start = audio.Min(t => t.OffsetSeconds);
        var end = audio.Max(t => t.EndSeconds);
        return video.OffsetSeconds <= start + SpanTolerance && (video.EndSeconds <= 0 || end <= 0 || video.EndSeconds >= end - SpanTolerance);
    }

    public static bool IsBroadcast(RecordingTrack track) => track.Kind is SourceKind.BrowserTab or SourceKind.Stream;

    /// <summary>The picture of a mix: the screen or a window first, then a tab or a stream; the earliest of them.</summary>
    public static RecordingTrack? Picture(IEnumerable<RecordingTrack> tracks) => tracks.Where(t => t.HasVideo)
        .OrderBy(t => t.Kind is SourceKind.Screen or SourceKind.Window ? 0 : 1).ThenBy(t => t.OffsetSeconds).FirstOrDefault();
}

/// <summary>An input of a mix: the list of a track's segments, its start relative to the mix and its length, in seconds.</summary>
public sealed record MixInput(string List, double Shift, double Duration);

/// <summary>
/// Mixes tracks into one file with FFmpeg. Every track is placed by its start on the session timeline, so a source that
/// joined mid-recording is heard where it was said. A screen or window video (H.264 recorded by CallDock) takes the mix
/// into its own file: the picture is copied, never stored twice. Any other picture is converted to H.264 in a separate
/// MP4; sound alone becomes an M4A. The result is checked against the expected length before it replaces anything.
/// </summary>
public static class Mixer
{
    public const string AudioFile = "mix.m4a";
    public const string VideoFile = "mix.mp4";

    /// <summary>The mix of a recording, kept with it: it replaces the previous mix and is returned for the session to remember.</summary>
    public static async Task<SessionMix> MixAsync(Archive archive, CallSession session, MixPlan plan, IProgress<double>? progress, CancellationToken ct)
    {
        var folder = archive.Folder(session);
        var temporary = Path.Combine(folder, $".mix-{Guid.NewGuid():N}{(plan.Video is null ? ".m4a" : ".mp4")}");
        try
        {
            var rendered = await RenderAsync(archive, session, plan, temporary, progress, ct);
            var inPlace = plan.Video is { Kind: SourceKind.Screen or SourceKind.Window } && rendered.VideoCopied;
            await ForgetAsync(archive, session, keepTrackId: inPlace ? plan.Video!.Id : null, ct);
            var mix = new SessionMix
            {
                OffsetSeconds = rendered.Origin, HasVideo = plan.Video is not null,
                Tracks = plan.Audio.Select(t => t.Id).ToList(), CreatedAt = DateTimeOffset.Now
            };
            if (inPlace)
            {
                var segments = MediaTools.TrackFiles(archive.TrackFolder(session, plan.Video!));
                File.Move(temporary, segments[0], true);
                foreach (var extra in segments.Skip(1)) File.Delete(extra);
                plan.Video!.HasMixedAudio = plan.Audio.Count > 0;
                mix.TrackId = plan.Video.Id;
            }
            else
            {
                var name = plan.Video is null ? AudioFile : VideoFile;
                File.Move(temporary, Path.Combine(folder, name), true);
                mix.File = name;
            }
            return mix;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>What a render did: where the result starts on the session timeline, and whether the picture was copied as is.</summary>
    public sealed record Rendered(double Origin, bool VideoCopied);

    /// <summary>Renders a mix into <paramref name="output"/> — its format follows the extension — and checks its length.</summary>
    public static async Task<Rendered> RenderAsync(Archive archive, CallSession session, MixPlan plan, string output, IProgress<double>? progress, CancellationToken ct)
    {
        if (plan.Audio.Count == 0 && plan.Video is null) throw new InvalidOperationException("Выберите дорожки для сведения.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(folder);
        var lists = new List<string>();
        try
        {
            var origin = plan.Video?.OffsetSeconds ?? plan.Audio.Min(t => t.OffsetSeconds);
            async Task<MixInput> InputAsync(RecordingTrack track)
            {
                var files = MediaTools.TrackFiles(archive.TrackFolder(session, track));
                if (files.Count == 0) throw new InvalidOperationException($"У дорожки «{track.Name}» нет записанных данных.");
                lists.Add(await MediaTools.ConcatListAsync(files, folder));
                double duration = 0;
                foreach (var file in files) duration += await MediaTools.DurationAsync(file, ct);
                return new(lists[^1], track.OffsetSeconds - origin, duration);
            }

            MixInput? video = null;
            var copy = false;
            if (plan.Video is { } picture)
            {
                video = await InputAsync(picture);
                copy = await MediaTools.VideoCodecAsync(MediaTools.TrackFiles(archive.TrackFolder(session, picture))[0], ct) is "h264" or "hevc";
            }
            var audio = new List<MixInput>();
            foreach (var track in plan.Audio) audio.Add(await InputAsync(track));
            var expected = video?.Duration ?? audio.Max(a => a.Shift + a.Duration);

            await MediaTools.RunWithProgressAsync(Arguments(video, copy, audio, output), expected, progress, ct);
            var actual = await MediaTools.DurationAsync(output, ct);
            if (Math.Abs(actual - expected) > Math.Max(1, expected * 0.01))
                throw new InvalidDataException(string.Create(CultureInfo.GetCultureInfo("ru-RU"),
                    $"Сведённый файл не совпал по длине с исходными дорожками ({actual:F1} с вместо {expected:F1} с). Исходные дорожки не тронуты."));
            return new(origin, copy);
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
        finally { foreach (var list in lists) File.Delete(list); }
    }

    /// <summary>
    /// Removes the previous mix before a new one takes its place: a separate file is deleted, a screen video gets its
    /// picture back without the mixed sound. <paramref name="keepTrackId"/> is the screen video the new mix will replace anyway.
    /// </summary>
    public static async Task ForgetAsync(Archive archive, CallSession session, string? keepTrackId, CancellationToken ct)
    {
        if (session.Mix?.File is { } file && File.Exists(Path.Combine(archive.Folder(session), file)))
            File.Delete(Path.Combine(archive.Folder(session), file));
        foreach (var track in session.Tracks.Where(t => t.HasMixedAudio && t.Id != keepTrackId))
        {
            var segments = MediaTools.TrackFiles(archive.TrackFolder(session, track));
            if (segments.Count == 0) continue;
            var temporary = segments[0] + ".video.mp4";
            try
            {
                await MediaTools.RunAsync(AppPaths.Ffmpeg,
                    ["-hide_banner", "-loglevel", "error", "-y", "-i", segments[0], "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart", temporary], ct);
                File.Move(temporary, segments[0], true);
                track.HasMixedAudio = false;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        session.Mix = null;
    }

    /// <summary>
    /// The FFmpeg command of a mix. Each sound is converted to 48 kHz stereo, delayed (or trimmed) to its place relative to
    /// the start of the mix, and summed without lowering anyone's volume; a limiter keeps the sum from clipping. Over a
    /// picture the sound is padded to the end of the video, which sets the length of the result. The sound format follows
    /// the extension of <paramref name="output"/>: MP4 and M4A get AAC, MP3, FLAC (24 bit) and WAV are also possible.
    /// </summary>
    public static IReadOnlyList<string> Arguments(MixInput? video, bool copyVideo, IReadOnlyList<MixInput> audio, string output)
    {
        var extension = Path.GetExtension(output).ToLowerInvariant();
        string[] codec = extension switch
        {
            ".mp4" or ".m4a" => ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
            ".mp3" => ["-c:a", "libmp3lame", "-b:a", "192k"],
            ".flac" => ["-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24"],
            ".wav" => ["-c:a", "pcm_f32le", "-rf64", "auto"],
            _ => throw new ArgumentException("Сведение сохраняется в MP4, M4A, MP3, FLAC или WAV.")
        };
        if (video is not null && extension != ".mp4") throw new ArgumentException("Видео со звуком сохраняется в MP4.");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (video is not null) args.AddRange(["-f", "concat", "-safe", "0", "-i", video.List]);
        foreach (var input in audio) args.AddRange(["-f", "concat", "-safe", "0", "-i", input.List]);
        var first = video is null ? 0 : 1;
        if (audio.Count > 0)
        {
            var chains = audio.Select((input, i) =>
                $"[{first + i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,{Shift(input.Shift)}[a{i}]");
            var sum = audio.Count == 1
                ? "[a0]anull"
                : string.Concat(audio.Select((_, i) => $"[a{i}]")) + $"amix=inputs={audio.Count}:duration=longest:dropout_transition=0:normalize=0";
            args.AddRange(["-filter_complex", string.Join(';', chains) + ";" + sum + ",alimiter=limit=0.97:level=false" + (video is null ? "" : ",apad") + "[mix]"]);
        }
        if (video is not null)
            args.AddRange(copyVideo ? ["-map", "0:v:0", "-c:v", "copy"] : ["-map", "0:v:0", "-c:v", "h264_mf", "-b:v", "4000k"]);
        if (audio.Count > 0) args.AddRange(["-map", "[mix]", .. codec]);
        else if (extension == ".mp4") args.AddRange(["-movflags", "+faststart"]);
        if (video is not null && audio.Count > 0) args.Add("-shortest");
        args.Add(output);
        return args;
    }

    private static string Shift(double seconds) => seconds switch
    {
        >= 0.001 => "adelay=" + Math.Round(seconds * 1000).ToString(CultureInfo.InvariantCulture) + ":all=1",
        <= -0.001 => "atrim=start=" + (-seconds).ToString("0.###", CultureInfo.InvariantCulture) + ",asetpts=PTS-STARTPTS",
        _ => "anull"
    };
}
