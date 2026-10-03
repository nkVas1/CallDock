using CallDock.Core;

namespace CallDock.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CallDock-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void RussianTranscriptIsSearchableAndReindexDoesNotDuplicate()
    {
        var archive = new Archive(root);
        var session = archive.Create("Стратегическая встреча", "Команда", "саммит");
        session.Transcript.Add(new(1, 3, "Спикер", "Обсудили строительство загородных домов"));
        archive.Save(session); archive.Save(session);
        Assert.Single(archive.Search("строитель"));
        Assert.Single(archive.Search("Команда домов"));
        Assert.Empty(archive.Search("' OR 1=1 --"));
        Assert.Empty(archive.Search("\" AND ( *"));
    }

    [Fact]
    public void EditingRemovesStaleSearchTokens()
    {
        var archive = new Archive(root);
        var session = archive.Create("Удаляемое слово", "", "");
        session.Title = "Новая встреча"; archive.Save(session);
        Assert.Empty(archive.Search("Удаляемое"));
        Assert.Single(archive.Search("Новая"));
    }

    [Fact]
    public void RecoveryRebuildsIndexAndMarksInterruptedRecordings()
    {
        var archive = new Archive(root);
        var session = archive.Create("Восстановление", "", "");
        session.TranscriptionStatus = "Running"; archive.Save(session);
        File.Delete(Path.Combine(root, "archive.sqlite3"));
        var fresh = new Archive(root);
        Assert.Single(fresh.Recover());
        Assert.Equal("Interrupted", fresh.Get(session.Id)!.Status);
        Assert.Equal("Queued", fresh.Get(session.Id)!.TranscriptionStatus);
        Assert.Single(fresh.Search("восстановление"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\Windows\\system.ini")]
    public void ArchivePathCannotEscapeRoot(string relative) => Assert.Throws<InvalidDataException>(() => AppPaths.Within(root, relative));

    [Fact]
    public void SubtitleTimesHandleRoundingAndMoreThan24Hours()
    {
        var session = new CallSession { Title = "Test", Transcript = [new(25 * 3600 + 59.9996, 25 * 3600 + 61, "A", "Привет")] };
        Assert.Contains("25:01:00,000 --> 25:01:01,000", ExportService.Transcript(session, ".srt"));
        Assert.StartsWith("WEBVTT", ExportService.Transcript(session, ".vtt"));
        Assert.Contains("Привет", ExportService.Transcript(session, ".txt"));
    }
}
