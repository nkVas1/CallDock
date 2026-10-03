using CallDock.Core;
using CallDock.Worker;
using NAudio.Wave;
using System.Buffers.Binary;

namespace CallDock.Tests;

public sealed class AudioTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CallDock-pcm-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void GapsSilenceOverlapsTrimAndSegmentsKeepExactLength()
    {
        var format = new WaveFormat(8000, 16, 1);
        var data = new byte[8000];
        for (int i = 0; i < data.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i), 8192);
        using (var writer = new PcmTimelineWriter(root, format, segmentSeconds: 1))
        {
            writer.WriteAt(4000, data); // first 0.5s silence, then 0.5s tone
            writer.WriteAt(6000, data); // overlap 0.25s, keep 0.25s
            writer.PadTo(12000);       // final 0.25s silence
            Assert.Equal(12000, writer.FramesWritten);
        }
        var files = Directory.GetFiles(root).Order().ToArray(); Assert.Equal(2, files.Length);
        using var a = new WaveFileReader(files[0]); using var b = new WaveFileReader(files[1]);
        Assert.Equal(1, a.TotalTime.TotalSeconds); Assert.Equal(0.5, b.TotalTime.TotalSeconds);
        var samples = new byte[16000]; a.ReadExactly(samples);
        Assert.All(samples.Take(8000), value => Assert.Equal(0, value));
        Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(8000)));
    }

    [Fact]
    public void WaveHeaderRecoversAfterSimulatedCrash()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "crash.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1))) writer.Write(new byte[32000]);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0);
        var dataOffset = bytes.AsSpan().IndexOf("data"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(dataOffset + 4), 0);
        File.WriteAllBytes(path, bytes);
        Assert.True(PcmTimelineWriter.RepairWave(path));
        using var recovered = new WaveFileReader(path);
        Assert.Equal(1, recovered.TotalTime.TotalSeconds);
    }

    [Fact]
    public void MeterReadsFloatPcmAndSilenceDoesNotTriggerTranscription()
    {
        var f = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var data = new byte[8]; BitConverter.GetBytes(-0.5f).CopyTo(data, 0);
        Assert.Equal(0.5f, AudioCapture.PcmPeak(data, f));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "silent.wav");
        using (var wav = new WaveFileWriter(path, new WaveFormat(16000, 16, 1))) wav.Write(new byte[64000]);
        Assert.False(Transcriber.ContainsSignal(path));
    }

    [Fact]
    public void MeterShowsTheLoudestSampleSinceLastReadThenResets()
    {
        var meter = new PeakMeter();
        meter.Feed(0.2f);
        meter.Feed(0.6f);
        meter.Feed(0.1f); // a quieter packet after a loud one must not hide it
        Assert.Equal(0.6f, meter.Take());
        Assert.Equal(0f, meter.Take());
        meter.Feed(float.NaN);
        Assert.Equal(0f, meter.Take());
    }

    [Theory]
    [InlineData("file:///C:/secrets")]
    [InlineData("ftp://example.com/video")]
    [InlineData("https://user:password@example.com/file")]
    [InlineData("-i something")]
    public void DirectStreamRequiresHttpAndNoInlineCredentials(string url) => Assert.Throws<ArgumentException>(() => MediaTools.ValidateStreamUrl(url));
}
