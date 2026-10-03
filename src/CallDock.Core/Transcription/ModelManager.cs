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

    public static void Delete(SpeechModel model)
    {
        if (File.Exists(PathFor(model))) File.Delete(PathFor(model));
    }

    public static async Task DownloadAsync(SpeechModel model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.Models);
        AppPaths.EnsureSpace(AppPaths.Models, model.Size + 512L * 1024 * 1024);
        var temporary = PathFor(model) + ".download";
        using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var response = await client.GetAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/{model.File}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                long downloaded = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    downloaded += count;
                    if (downloaded > model.Size) throw new InvalidDataException("Размер модели не совпадает с ожидаемым.");
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress?.Report((double)downloaded / model.Size);
                }
                if (downloaded != model.Size) throw new InvalidDataException("Загрузка модели не завершена.");
            }
            await using (var file = File.OpenRead(temporary))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Проверка SHA-256 модели не пройдена.");
            }
            File.Move(temporary, PathFor(model), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
