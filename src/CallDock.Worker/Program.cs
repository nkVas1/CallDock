using CallDock.Core;
using CallDock.Worker;
using System.Text.Json;
using NAudio.Wave;

if (args.Length == 2 && args[0] == "download-model")
{
    await ModelManager.DownloadAsync(ModelManager.Find(args[1]));
    return 0;
}

if (args.Length == 3 && args[0] == "qa-tone")
{
    using var player = new WasapiPlayerBuilder().WithSharedMode().Build();
    player.Init(new TestTone(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)));
    await Task.Delay(800);
    player.Play();
    await Task.Delay(int.Parse(args[2]));
    player.Stop();
    return 0;
}

// Transcription runs here, not in the app: a native inference failure can end this process without taking down a recording.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
if (args.Length != 4 || args[0] != "transcribe") return 2;
try
{
    var archive = new Archive(args[1]);
    var session = archive.Get(args[2]) ?? throw new InvalidOperationException("Запись не найдена.");
    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[3]), AppPaths.Json)!;
    // One line per update, read by ProcessingQueue: "progress <fraction 0..1> <what is being transcribed>".
    var transcript = await Transcriber.RunAsync(archive, session, settings, p =>
    {
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"progress {p.Fraction:F4} {p.Text}"));
        Console.Out.Flush();
    }, CancellationToken.None);
    AppPaths.AtomicJson(Path.Combine(archive.Folder(session), "transcript-result.json"), transcript);
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

internal sealed class TestTone(double frequency) : IWaveProvider
{
    private long sample;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int Read(Span<byte> buffer)
    {
        for (int i = 0; i + 7 < buffer.Length; i += 8)
        {
            var value = (float)(0.015 * Math.Sin(2 * Math.PI * frequency * sample++ / 48000));
            BitConverter.TryWriteBytes(buffer.Slice(i, 4), value);
            BitConverter.TryWriteBytes(buffer.Slice(i + 4, 4), value);
        }
        return buffer.Length;
    }
}
