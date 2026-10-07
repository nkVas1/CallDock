using System.Text.RegularExpressions;

namespace CallDock.Core;

/// <summary>
/// Corrections of a transcript by the person: the text of a phrase, the speaker of a phrase, the name of a speaker. The
/// recognized text stays beside a correction, and every phrase keeps the track it was heard on, so it still plays from
/// there whoever it is given to.
/// </summary>
public static partial class TranscriptEditing
{
    public const int MaxText = 2000;
    public const int MaxName = 80;

    /// <summary>A correction as it is stored: one line, no stray spaces, a bounded length.</summary>
    public static string CleanText(string text)
    {
        var clean = Spaces().Replace(text, " ").Trim();
        return clean.Length > MaxText ? clean[..MaxText].TrimEnd() : clean;
    }

    public static string CleanName(string name)
    {
        var clean = Spaces().Replace(name, " ").Trim();
        return clean.Length > MaxName ? clean[..MaxName].TrimEnd() : clean;
    }

    /// <summary>The phrase with corrected text. The recognized text is kept until the phrase reads as recognized again.</summary>
    public static TranscriptSegment WithText(TranscriptSegment segment, string text)
    {
        var recognized = segment.Original ?? segment.Text;
        return segment with { Text = text, Original = text == recognized ? null : recognized };
    }

    /// <summary>The phrase as it was recognized.</summary>
    public static TranscriptSegment Recognized(TranscriptSegment segment) =>
        segment.Original is { } recognized ? segment with { Text = recognized, Original = null } : segment;

    /// <summary>The track a phrase was heard on: by its id, or — in transcripts of older versions — the track of its
    /// speaker that covers its moment (a source switched off and on has several).</summary>
    public static RecordingTrack? TrackOf(CallSession session, TranscriptSegment segment)
    {
        if (segment.Track is { } id) return session.Tracks.FirstOrDefault(t => t.Id == id);
        var tracks = session.Tracks.Where(t => t.Name == segment.Source && t.HasAudio).OrderBy(t => t.OffsetSeconds).ToArray();
        return tracks.LastOrDefault(t => t.OffsetSeconds <= segment.Start + 0.5) ?? tracks.FirstOrDefault();
    }

    /// <summary>The phrase given to another speaker; it remembers its track first.</summary>
    public static TranscriptSegment Assign(CallSession session, TranscriptSegment segment, string speaker) =>
        segment with { Source = speaker, Track = segment.Track ?? TrackOf(session, segment)?.Id };

    /// <summary>
    /// Renames a speaker throughout a recording: the phrases and the tracks with that name, so a new transcription uses
    /// the new name too. A name that is already there joins the two speakers. Returns how many phrases were renamed.
    /// </summary>
    public static int RenameSpeaker(CallSession session, string from, string to)
    {
        var renamed = 0;
        for (var i = 0; i < session.Transcript.Count; i++)
        {
            if (session.Transcript[i].Source != from) continue;
            session.Transcript[i] = Assign(session, session.Transcript[i], to);
            renamed++;
        }
        foreach (var track in session.Tracks.Where(t => t.Name == from)) track.Name = to;
        return renamed;
    }

    /// <summary>
    /// What a correction did when it replaced a word, or up to three words in a row («Стройк» → «Строик»): the words
    /// before and after, as written. Null for any other change — words only added or removed, punctuation, several places
    /// at once — and for words too short to replace everywhere safely («в» → «на»).
    /// </summary>
    public static (string From, string To)? ChangedWords(string before, string after)
    {
        var a = Words().Matches(before);
        var b = Words().Matches(after);
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix].Value == b[prefix].Value) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)].Value == b[^(suffix + 1)].Value) suffix++;
        int removed = a.Count - prefix - suffix, added = b.Count - prefix - suffix;
        if (removed is < 1 or > 3 || added is < 1 or > 3) return null;
        // A word left as it was inside the changed part means corrections in several places, not one replacement.
        var kept = Enumerable.Range(prefix, removed).Select(i => a[i].Value).ToHashSet();
        if (Enumerable.Range(prefix, added).Any(i => kept.Contains(b[i].Value))) return null;
        var from = before[a[prefix].Index..(a[prefix + removed - 1].Index + a[prefix + removed - 1].Length)];
        var to = after[b[prefix].Index..(b[prefix + added - 1].Index + b[prefix + added - 1].Length)];
        if (from.Count(char.IsLetterOrDigit) < 3 || from == to) return null;
        // A capital letter at the start of a sentence is grammar, not a name: not a correction to repeat everywhere.
        return string.Equals(from, to, StringComparison.CurrentCultureIgnoreCase) && SentenceStart(after, b[prefix].Index) ? null : (from, to);
    }

    /// <summary>Nothing but spaces, quotes and dashes since the start of the text or the end of the previous sentence.</summary>
    private static bool SentenceStart(string text, int index)
    {
        var before = text[..index].TrimEnd(' ', ' ', '«', '"', '„', '(', '—', '–', '-');
        return before.Length == 0 || before[^1] is '.' or '!' or '?' or '…';
    }

    /// <summary>
    /// The text with every whole-word occurrence of <paramref name="from"/>, in any letter case, replaced by
    /// <paramref name="to"/>. An occurrence that starts with a capital letter — the start of a sentence — keeps it.
    /// </summary>
    public static string ReplaceWords(string text, string from, string to) =>
        Occurrence(from).Replace(text, match => to.Length > 0 && char.IsUpper(match.Value[0]) && char.IsLower(to[0]) ? char.ToUpper(to[0]) + to[1..] : to);

    /// <summary>How many phrases a replacement would change.</summary>
    public static int CountReplacements(IEnumerable<TranscriptSegment> transcript, string from, string to)
    {
        var occurrence = Occurrence(from);
        return transcript.Count(s => occurrence.IsMatch(s.Text) && ReplaceWords(s.Text, from, to) != s.Text);
    }

    private static Regex Occurrence(string words) =>
        new($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(words)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Words with their inner hyphens and apostrophes: «кто-то», «O'Brien».</summary>
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-'’][\p{L}\p{N}]+)*")]
    private static partial Regex Words();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
