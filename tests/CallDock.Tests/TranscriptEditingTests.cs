using CallDock.Core;
using System.Text.Json;

namespace CallDock.Tests;

public sealed class TranscriptEditingTests
{
    [Theory]
    [InlineData("Компания Стройк выиграла тендер", "Компания Строик выиграла тендер", "Стройк", "Строик")]
    [InlineData("встреча в строй групп завтра", "встреча в СтройГрупп завтра", "строй групп", "СтройГрупп")]
    [InlineData("звонил иван вчера", "звонил Иван вчера", "иван", "Иван")]
    public void AReplacedWordIsFound(string before, string after, string from, string to) =>
        Assert.Equal((from, to), TranscriptEditing.ChangedWords(before, after));

    [Theory]
    [InlineData("пошли в офис", "пошли на офис")]                 // too short to replace everywhere
    [InlineData("да конечно", "да, конечно")]                     // punctuation only
    [InlineData("и вот. конечно приду", "и вот. Конечно приду")]  // the start of a sentence
    [InlineData("мы приедем", "мы точно приедем")]                // a word added
    [InlineData("один два три", "раз два четыре")]                // two places
    public void OtherCorrectionsAreNotOfferedEverywhere(string before, string after) =>
        Assert.Null(TranscriptEditing.ChangedWords(before, after));

    [Fact]
    public void ReplacementTakesWholeWordsAndKeepsTheCapitalOfASentence()
    {
        Assert.Equal("Строик — это мы. А строик это бренд, не перестройка",
            TranscriptEditing.ReplaceWords("Стройк — это мы. А стройк это бренд, не перестройка", "стройк", "строик"));
        var transcript = new[] { Phrase(0, "Я", "Стройк и Строик"), Phrase(2, "Я", "без него") };
        Assert.Equal(1, TranscriptEditing.CountReplacements(transcript, "Стройк", "Строик"));
        Assert.Equal(0, TranscriptEditing.CountReplacements([Phrase(0, "Я", "уже Строик")], "строик", "Строик"));
    }

    [Fact]
    public void TheRecognizedTextStaysUntilThePhraseReadsSoAgain()
    {
        var phrase = Phrase(0, "Я", "Стройк выиграл");
        var once = TranscriptEditing.WithText(phrase, "Строик выиграл");
        var twice = TranscriptEditing.WithText(once, "Строик выиграл тендер");
        Assert.Equal("Стройк выиграл", twice.Original);
        Assert.Null(TranscriptEditing.WithText(twice, "Стройк выиграл").Original);
        Assert.Equal(phrase, TranscriptEditing.Recognized(twice));
    }

    [Fact]
    public void RenamingASpeakerRenamesItsTracksAndKeepsEachPhraseOnItsTrack()
    {
        var (session, mic, first, second) = Recording();
        Assert.Equal(2, TranscriptEditing.RenameSpeaker(session, "Собеседники", "Иван"));
        Assert.All(session.Tracks.Where(t => t.Kind == SourceKind.SystemAudio), t => Assert.Equal("Иван", t.Name));
        Assert.Equal("Я", mic.Name);
        Assert.Equal([first.Id, second.Id], session.Transcript.Where(s => s.Source == "Иван").Select(s => s.Track));
    }

    [Fact]
    public void APhraseGivenToAnotherSpeakerStillPlaysFromWhereItWasHeard()
    {
        var (session, _, _, second) = Recording();
        var late = session.Transcript[2];
        var given = TranscriptEditing.Assign(session, late, "Мария");
        Assert.Equal("Мария", given.Source);
        Assert.Equal(second.Id, given.Track);              // the track switched on again covers 25 s
        Assert.Equal(second, TranscriptEditing.TrackOf(session, given));
    }

    [Fact]
    public void TranscriptsWithoutCorrectionsKeepTheirFormat()
    {
        var json = JsonSerializer.Serialize(new TranscriptSegment(1, 2, "Я", "текст"), AppPaths.Json);
        Assert.DoesNotContain("Original", json);
        Assert.DoesNotContain("Track", json);
        var old = JsonSerializer.Deserialize<TranscriptSegment>("""{"Start":1,"End":2,"Source":"Я","Text":"текст"}""", AppPaths.Json)!;
        Assert.Null(old.Track);
        Assert.Null(old.Original);
    }

    private static TranscriptSegment Phrase(double start, string speaker, string text) => new(start, start + 1, speaker, text);

    /// <summary>«Я» on the microphone; «Собеседники» on the system sound, switched off at 15 s and on again at 20 s.
    /// The phrases come from a transcript of an older version: no tracks in them.</summary>
    private static (CallSession Session, RecordingTrack Mic, RecordingTrack First, RecordingTrack Second) Recording()
    {
        var mic = new RecordingTrack { Name = "Я", Kind = SourceKind.Microphone };
        var first = new RecordingTrack { Name = "Собеседники", Kind = SourceKind.SystemAudio, EndSeconds = 15 };
        var second = new RecordingTrack { Name = "Собеседники", Kind = SourceKind.SystemAudio, OffsetSeconds = 20 };
        var session = new CallSession
        {
            Tracks = [mic, first, second],
            Transcript = [Phrase(1, "Я", "добрый день"), Phrase(5, "Собеседники", "здравствуйте"), Phrase(25, "Собеседники", "до встречи")]
        };
        return (session, mic, first, second);
    }
}
