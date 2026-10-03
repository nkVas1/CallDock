using CallDock.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace CallDock.App.Services;

public sealed class AudioPreview : IAsyncDisposable
{
    private WasapiRecorder? recorder;
    private MMDevice? device;
    private readonly PeakMeter meter = new();
    /// <summary>The loudest sample since the previous read (0…1).</summary>
    public float Peak => meter.Take();
    public string? Error { get; private set; }
    public static async Task<AudioPreview> StartAsync(SourceSpec spec)
    {
        var preview = new AudioPreview();
        try
        {
            await Task.Run(async () =>
            {
                var builder = new WasapiRecorderBuilder().WithBufferLength(40).WithMmcssThreadPriority("Audio");
                if (spec.Kind == SourceKind.Application)
                {
                    if (!Capabilities.ApplicationAudio) throw new PlatformNotSupportedException(Capabilities.ApplicationAudioHint);
                    builder.WithProcessLoopback(uint.Parse(spec.Target)).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
                }
                else
                {
                    using var devices = new MMDeviceEnumerator();
                    preview.device = devices.GetDevice(spec.Target);
                    builder.WithDevice(preview.device);
                    if (spec.Kind == SourceKind.SystemAudio) builder.WithLoopbackCapture();
                }
                preview.recorder = await builder.BuildAsync();
                preview.recorder.DataAvailable += (data, _, _, _) =>
                {
                    if (!data.IsEmpty) preview.meter.Feed(AudioCapture.PcmPeak(data, preview.recorder.WaveFormat));
                };
                preview.recorder.RecordingStopped += (_, e) => preview.Error = e.Exception?.Message;
                preview.recorder.StartRecording();
            });
            return preview;
        }
        catch { await preview.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync() { if (recorder is not null) await recorder.DisposeAsync(); device?.Dispose(); }
}
