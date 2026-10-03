using CallDock.Core;
using CallDock.Worker;
using System.Text.Json;

namespace CallDock.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CallDock-storage-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void InterruptedCompressionNeverDoublesASegment()
    {
        Directory.CreateDirectory(root);
        // 00000 was compressed and its original removed; 00001 was compressed but the original is still there.
        foreach (var name in new[] { "00000.flac", "00001.wav", "00001.flac", "00002.wav", "00001.flac.part", "notes.txt" })
            File.WriteAllText(Path.Combine(root, name), "");
        var files = MediaTools.TrackFiles(root).Select(f => Path.GetFileName(f)).ToArray();
        Assert.Equal(["00000.flac", "00001.wav", "00002.wav"], files);
    }

    [Theory]
    [InlineData("Субтитры сделал DimaTorzok", true)]
    [InlineData("Редактор субтитров А.Синецкая Корректор А.Егорова", true)]
    [InlineData("Продолжение следует...", true)]
    [InlineData("Продолжение следует на следующей встрече, договорились", false)]
    [InlineData("Спасибо за внимание, вопросы?", false)]
    [InlineData("Обсудили смету и сроки по объекту", false)]
    public void KnownWhisperArtefactsAreDroppedAndSpeechIsKept(string text, bool artefact) =>
        Assert.Equal(artefact, Transcriber.IsHallucination(text));

    [Fact]
    public void SettingsFromTheFirstVersionStillLoad()
    {
        // 1.0 prototype settings: sources without labels, no new options.
        const string json = """
            { "ArchiveRoot": "D:\\Calls", "Model": "tiny", "SavedSources": [ { "Kind": 0, "Name": "USB Mic", "Target": "{0.0.1}" } ] }
            """;
        var settings = JsonSerializer.Deserialize<AppSettings>(json, AppPaths.Json)!;
        Assert.Equal("D:\\Calls", settings.ArchiveRoot);
        Assert.True(settings.CompressAudio);
        var source = Assert.Single(settings.SavedSources);
        Assert.Null(source.Label);
        Assert.Equal("USB Mic", source.DisplayName);
        Assert.Equal("Microphone:{0.0.1}", source.Key);
    }

    [Fact]
    public void ProjectsAreSuggestedMostRecentFirstAndDeleteRemovesFromSearch()
    {
        var archive = new Archive(root);
        var first = archive.Create("Планёрка", "Ремонт", "");
        first.Status = SessionStatus.Done; archive.Save(first);
        var second = archive.Create("Созвон с поставщиком", "Закупки", "");
        second.StartedAt = first.StartedAt.AddMinutes(5); second.Status = SessionStatus.Done; archive.Save(second);
        Assert.Equal(["Закупки", "Ремонт"], archive.Projects());

        archive.Delete(second, toRecycleBin: false);
        Assert.Empty(archive.Search("поставщиком"));
        Assert.Null(archive.Get(second.Id));
        Assert.False(Directory.Exists(archive.Folder(second)));
    }
}
