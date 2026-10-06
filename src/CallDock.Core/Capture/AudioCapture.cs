using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;

namespace CallDock.Core;

public interface IRecording : IAsyncDisposable
{
    RecordingTrack Track { get; }
    float Peak { get; }
    long BytesWritten { get; }
    string? Error { get; }
    Task StopAsync();
}

public static class AudioDevices
{
    /// <summary>Microphones, playback devices (their «system sound»), and — where Windows allows — applications with a window.</summary>
    public static IReadOnlyList<SourceSpec> List()
    {
        var result = new List<SourceSpec>();
        using (var devices = new MMDeviceEnumerator())
        {
            foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
                foreach (var device in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    using (device) result.Add(new(flow == DataFlow.Capture ? SourceKind.Microphone : SourceKind.SystemAudio,
                        device.FriendlyName, device.ID));
                }
        }
        if (!Capabilities.ApplicationAudio) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses().OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    var app = ApplicationName(process);
                    if (!seen.Add(app + "|" + process.MainWindowTitle)) continue;
                    result.Add(new(SourceKind.Application, $"{app} — {process.MainWindowTitle}", process.Id.ToString(), app));
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return result;
    }

    /// <summary>«Telegram Desktop» rather than «Telegram»; falls back to the process name for protected processes.</summary>
    private static string ApplicationName(Process process)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return process.ProcessName;
    }

    /// <summary>The default microphone and playback device: what a call needs out of the box («Я» and «Собеседники»).</summary>
    public static IReadOnlyList<SourceSpec> Defaults()
    {
        using var devices = new MMDeviceEnumerator();
        var result = new List<SourceSpec>();
        if (devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            using (var mic = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                result.Add(new(SourceKind.Microphone, mic.FriendlyName, mic.ID, "Я"));
        if (devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            using (var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                result.Add(new(SourceKind.SystemAudio, output.FriendlyName, output.ID, "Собеседники"));
        return result;
    }
}

public sealed class AudioCapture : IRecording
{
    private readonly WasapiRecorder recorder;
    private readonly MMDevice? device;
    /// <summary>About a minute of sound (packets of ~10 ms): a slow disk shared with a screen recording can stall writing
    /// for seconds, and the sound waits here instead of being lost.</summary>
    private readonly Channel<Packet> packets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(6000) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly PcmTimelineWriter writer;
    private readonly long startedQpc;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Task consumer;
    private Task? stopTask;
    private readonly PeakMeter meter = new();
    private long bytes;
    private long droppedFrames;
    private string? error;
    private readonly object stopGate = new();
    private sealed record Packet(byte[] Data, long Frame);

    public RecordingTrack Track { get; }
    /// <summary>The loudest sample since the previous read (0…1).</summary>
    public float Peak => meter.Take();
    public long BytesWritten => Interlocked.Read(ref bytes);
    public string? Error => Volatile.Read(ref error) ?? (Interlocked.Read(ref droppedFrames) is > 0 and var dropped
        ? $"Компьютер не успевал записывать звук: пропущено {dropped * 1000 / recorder.WaveFormat.SampleRate} мс, на их месте тишина."
        : null);
    public WaveFormat Format => recorder.WaveFormat;

    /// <summary>«48 кГц · стерео · WAV»: what the person needs to know about a sound track, without codec jargon.</summary>
    public static string Describe(WaveFormat format)
    {
        var channels = format.Channels switch { 1 => "моно", 2 => "стерео", var n => $"{n} каналов" };
        return string.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), $"{format.SampleRate / 1000.0:0.#} кГц · {channels} · WAV");
    }
    public long CaptureStartedAt { get; } = Stopwatch.GetTimestamp();

    private AudioCapture(WasapiRecorder recorder, MMDevice? device, RecordingTrack track, string folder)
    {
        this.recorder = recorder;
        this.device = device;
        Track = track;
        track.Format = Describe(recorder.WaveFormat);
        writer = new PcmTimelineWriter(folder, recorder.WaveFormat);
        startedQpc = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        recorder.DataAvailable += OnData;
        recorder.RecordingStopped += (_, e) => { if (e.Exception is not null) error = e.Exception.Message; };
        consumer = Task.Run(ConsumeAsync);
    }

    public static async Task<AudioCapture> StartAsync(SourceSpec spec, RecordingTrack track, string folder)
    {
        return await Task.Run(async () =>
        {
            MMDevice? device = null;
            WasapiRecorder? recorder = null;
            AudioCapture? capture = null;
            try
            {
                var builder = new WasapiRecorderBuilder().WithSharedMode().WithEventSync().WithBufferLength(40).WithMmcssThreadPriority("Audio");
                if (spec.Kind == SourceKind.Application)
                {
                    if (!Capabilities.ApplicationAudio) throw new PlatformNotSupportedException(Capabilities.ApplicationAudioHint);
                    builder.WithProcessLoopback(uint.Parse(spec.Target)).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
                }
                else
                {
                    using var enumerator = new MMDeviceEnumerator();
                    device = enumerator.GetDevice(spec.Target);
                    builder.WithDevice(device);
                    if (spec.Kind == SourceKind.SystemAudio) builder.WithLoopbackCapture();
                }
                recorder = await builder.BuildAsync();
                capture = new AudioCapture(recorder, device, track, folder);
                recorder.StartRecording();
                return capture;
            }
            catch
            {
                if (capture is not null) await capture.DisposeAsync();
                else { recorder?.Dispose(); device?.Dispose(); }
                throw;
            }
        });
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        // NAudio also signals empty buffers; they carry no audio and must not reset the meter.
        if (data.IsEmpty) return;
        var format = recorder.WaveFormat;
        var position = (long)Math.Round((qpcPosition - startedQpc) * (format.SampleRate / 10_000_000.0));
        if ((flags & AudioClientBufferFlags.TimestampError) != 0 || qpcPosition == 0)
            position = (long)(clock.Elapsed.TotalSeconds * format.SampleRate) - data.Length / format.BlockAlign;
        meter.Feed(PcmPeak(data, format));
        if (!packets.Writer.TryWrite(new Packet(data.ToArray(), position)))
            Interlocked.Add(ref droppedFrames, data.Length / format.BlockAlign);
    }

    public static float PcmPeak(ReadOnlySpan<byte> data, WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        float peak = 0;
        var width = format.BitsPerSample / 8;
        for (var i = 0; i + width <= data.Length; i += width)
        {
            float sample = width switch
            {
                4 when isFloat => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[i..])),
                4 => BinaryPrimitives.ReadInt32LittleEndian(data[i..]) / 2147483648f,
                3 => ((data[i] | data[i + 1] << 8 | data[i + 2] << 16) << 8 >> 8) / 8388608f,
                2 => BinaryPrimitives.ReadInt16LittleEndian(data[i..]) / 32768f,
                1 => (data[i] - 128) / 128f,
                _ => 0
            };
            if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
        }
        return Math.Min(1, peak);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            var flushed = Stopwatch.StartNew();
            while (!packets.Reader.Completion.IsCompleted)
            {
                while (packets.Reader.TryRead(out var packet)) writer.WriteAt(packet.Frame, packet.Data);
                // Loopback can stop producing packets during silence. Leave 500ms for delivery jitter.
                var elapsed = Math.Max(0, clock.Elapsed.TotalSeconds - 0.5);
                writer.PadTo((long)(elapsed * recorder.WaveFormat.SampleRate));
                // The header is rewritten once a second, not on every pass: each rewrite is a seek on a hard disk. A WAV
                // cut off by a crash is repaired on the next start anyway.
                if (flushed.Elapsed >= TimeSpan.FromSeconds(1)) { writer.Flush(); flushed.Restart(); }
                Interlocked.Exchange(ref bytes, writer.FramesWritten * recorder.WaveFormat.BlockAlign);
                await Task.Delay(100);
            }
            while (packets.Reader.TryRead(out var packet)) writer.WriteAt(packet.Frame, packet.Data);
            writer.PadTo((long)(clock.Elapsed.TotalSeconds * recorder.WaveFormat.SampleRate));
        }
        catch (Exception e) { error = "Ошибка сохранения аудио: " + e.Message; }
        finally { writer.Dispose(); }
    }

    public Task StopAsync() { lock (stopGate) return stopTask ??= StopCoreAsync(); }

    private async Task StopCoreAsync()
    {
        await recorder.DisposeAsync();
        clock.Stop();
        packets.Writer.TryComplete();
        await consumer;
        device?.Dispose();
        Track.Status = Error is null ? "Done" : "Failed";
        Track.Error = Error;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
