using CallDock.Core;
using NAudio.Wave;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CallDock.App.Services;

/// <summary>Hidden QA mode (<c>CallDock.exe --verify-hardware &lt;folder&gt;</c>): records a synthetic tone from one
/// process while another plays a different tone, and records a synthetic window — proving per-application
/// isolation and native window capture on a real machine. Needs Windows 11 for the audio part.</summary>
public static class HardwareVerification
{
    public static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var results = new List<object>();
        var worker = Path.Combine(AppContext.BaseDirectory, "CallDock.Worker.exe");
        using var wanted = Process.Start(MediaTools.StartInfo(worker, ["qa-tone", "440", "4500"]))!;
        using var unrelated = Process.Start(MediaTools.StartInfo(worker, ["qa-tone", "1000", "4500"]))!;
        var track = new RecordingTrack { Name = "Synthetic isolated tone", Kind = SourceKind.Application };
        var audioFolder = Path.Combine(folder, "audio");
        await using (var recording = await AudioCapture.StartAsync(new(SourceKind.Application, track.Name, wanted.Id.ToString()), track, audioFolder))
        {
            await Task.Delay(6200);
            await recording.StopAsync();
            if (recording.Error is not null) throw new InvalidOperationException(recording.Error);
        }
        await wanted.WaitForExitAsync(); await unrelated.WaitForExitAsync();
        using (var reader = new WaveFileReader(Directory.GetFiles(audioFolder, "*.wav")[0]))
        {
            var data = new byte[reader.Length]; reader.ReadExactly(data);
            var duration = reader.TotalTime.TotalSeconds;
            var wantedPower = FrequencyPower(data, reader.WaveFormat, 440);
            var otherPower = FrequencyPower(data, reader.WaveFormat, 1000);
            if (duration < 6 || wantedPower < 0.0001 || otherPower > wantedPower / 20)
                throw new InvalidOperationException($"Process isolation failed: duration={duration}, wanted={wantedPower}, unrelated={otherPower}");
            results.Add(new { test = "process-audio-isolation", duration, wantedPower, otherPower, passed = true });
        }

        var display = new TextBlock { Text = "CallDock capture verification\nSynthetic window only", FontSize = 28, Foreground = Brushes.White, Margin = new Thickness(30) };
        var window = new Window { Title = "CallDock synthetic capture target", Width = 640, Height = 400, Content = display, Background = Brushes.DarkSlateBlue };
        window.Show();
        await Task.Delay(500);
        var handle = new WindowInteropHelper(window).Handle;
        var videoTrack = new RecordingTrack { Name = "Synthetic window", Kind = SourceKind.Window };
        var videoFolder = Path.Combine(folder, "video");
        await using (var recording = new VideoCapture(new(SourceKind.Window, videoTrack.Name, handle.ToInt64().ToString()), videoTrack, videoFolder, 24))
        {
            await recording.WaitStartedAsync();
            for (var i = 0; i < 5; i++) { display.Text = $"CallDock capture verification\nFrame step {i + 1}"; await Task.Delay(700); }
            await recording.StopAsync();
            if (recording.Error is not null) throw new InvalidOperationException(recording.Error);
        }
        window.Close();
        var video = Path.Combine(videoFolder, "00000.mp4");
        var videoDuration = await MediaTools.DurationAsync(video);
        if (videoDuration < 3) throw new InvalidOperationException("Window recording too short.");
        results.Add(new { test = "native-window-capture", duration = videoDuration, bytes = new FileInfo(video).Length, passed = true });
        await MediaTools.RunAsync(AppPaths.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", video, "-f", "null", "-"]);
        await MediaTools.RunAsync(AppPaths.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", video, "-t", "2", "-c:v", "h264_mf", "-b:v", "2000k", Path.Combine(folder, "preview.mp4")]);
        results.Add(new { test = "video-decode-and-preview-encoder", passed = true });
        AppPaths.AtomicJson(Path.Combine(folder, "hardware-results.json"), results);
    }

    private static double FrequencyPower(byte[] data, WaveFormat format, double hz)
    {
        // Known synthetic test format, inspect a one-second interior window.
        var start = format.SampleRate * format.BlockAlign * 2;
        var frames = Math.Min(format.SampleRate, (data.Length - start) / format.BlockAlign);
        double sin = 0, cos = 0;
        for (int i = 0; i < frames; i++)
        {
            var value = BitConverter.ToSingle(data, start + i * format.BlockAlign);
            sin += value * Math.Sin(2 * Math.PI * hz * i / format.SampleRate);
            cos += value * Math.Cos(2 * Math.PI * hz * i / format.SampleRate);
        }
        return 2 * Math.Sqrt(sin * sin + cos * cos) / frames;
    }
}
