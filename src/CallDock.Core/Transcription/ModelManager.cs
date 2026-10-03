using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CallDock.Core;

public sealed record SpeechModel(string Id, string Title, string Description, string File, long Size, string Sha256)
{
    public string SizeLabel => Display.Size(Size);
    public override string ToString() => Title;
}

public static class ModelManager
{
    public static readonly SpeechModel[] Models =
    [
        new("large-v3-turbo-q5_0", "Высокое качество", "Whisper large-v3-turbo. Лучше всех понимает русскую речь и термины; нужен современный процессор.",
            "ggml-large-v3-turbo-q5_0.bin", 574041195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        new("small", "Баланс", "Whisper small. Заметно быстрее, ошибается чаще на именах и терминах.",
            "ggml-small.bin", 487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new("tiny", "Быстрая черновая", "Whisper tiny. Для слабых компьютеров и проверки; текст черновой.",
            "ggml-tiny.bin", 77691713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21")
    ];
    public static SpeechModel Find(string id) => Models.FirstOrDefault(x => x.Id == id) ?? Models[0];

    /// <summary>The model this computer runs at a usable speed. The large model wants eight or more logical
    /// processors: on a dual-core laptop it needs over ten minutes for a minute of speech.</summary>
    public static SpeechModel Recommended => Environment.ProcessorCount >= 8 ? Models[0] : Find("small");
    public static string PathFor(SpeechModel model) => Path.Combine(AppPaths.Models, model.File);
    public static bool IsInstalled(SpeechModel model) => File.Exists(PathFor(model)) && new FileInfo(PathFor(model)).Length == model.Size;

    private static string PartialPath(SpeechModel model) => PathFor(model) + ".download";

    /// <summary>Bytes of an interrupted download that the next one continues from.</summary>
    public static long PartialBytes(SpeechModel model) => File.Exists(PartialPath(model)) ? new FileInfo(PartialPath(model)).Length : 0;

    public static void Delete(SpeechModel model)
    {
        if (File.Exists(PathFor(model))) File.Delete(PathFor(model));
        if (File.Exists(PartialPath(model))) File.Delete(PartialPath(model));
    }

    /// <summary>A minute without a single byte means the connection (or a proxy) has stalled.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Downloads a model, continuing an interrupted download where it stopped: half a gigabyte over a shaky
    /// connection must not start over. The file is checked against its SHA-256 before it is used.</summary>
    public static async Task DownloadAsync(SpeechModel model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.Models);
        AppPaths.EnsureSpace(AppPaths.Models, model.Size + 512L * 1024 * 1024);
        var temporary = PartialPath(model);
        var downloaded = PartialBytes(model);
        if (downloaded > model.Size) { File.Delete(temporary); downloaded = 0; }
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (downloaded < model.Size)
            {
                stall.CancelAfter(StallTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/{model.File}");
                if (downloaded > 0) request.Headers.Range = new RangeHeaderValue(downloaded, null);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.PartialContent) downloaded = 0; // the whole file came: start over
                await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
                await using var file = new FileStream(temporary, downloaded > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                var buffer = new byte[131072];
                int count;
                progress?.Report((double)downloaded / model.Size);
                while ((count = await input.ReadAsync(buffer, stall.Token)) != 0)
                {
                    stall.CancelAfter(StallTimeout);
                    downloaded += count;
                    if (downloaded > model.Size) throw new InvalidDataException("Размер модели не совпадает с ожидаемым.");
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress?.Report((double)downloaded / model.Size);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Загрузка остановилась: больше минуты нет данных. Проверьте интернет или прокси и нажмите «Скачать» ещё раз — загрузка продолжится с того же места.");
        }
        catch (InvalidDataException)
        {
            File.Delete(temporary);
            throw;
        }
        if (downloaded != model.Size)
            throw new IOException("Загрузка прервалась. Нажмите «Скачать» ещё раз — она продолжится с того же места.");

        string hash;
        await using (var file = File.OpenRead(temporary)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        if (!hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporary);
            throw new InvalidDataException("Скачанный файл повреждён (не совпала контрольная сумма). Скачайте модель заново.");
        }
        File.Move(temporary, PathFor(model), true);
    }
}
