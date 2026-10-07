using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CallDock.Core;

public sealed partial class Archive
{
    private readonly string connectionString;
    private readonly object gate = new();
    public string Root { get; }

    public Archive(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "archive.sqlite3"), Pooling = false }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, started TEXT NOT NULL, folder TEXT NOT NULL, json TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(id UNINDEXED, text, tokenize='unicode61 remove_diacritics 2');
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }

    public CallSession Create(string title, string project, string tags)
    {
        AppPaths.EnsureSpace(Root);
        var session = new CallSession { Title = string.IsNullOrWhiteSpace(title) ? $"Звонок {DateTime.Now:dd.MM HH:mm}" : title.Trim(), Project = project.Trim(), Tags = tags.Trim() };
        session.Folder = Path.Combine(session.StartedAt.ToString("yyyy"), session.StartedAt.ToString("MM"), session.StartedAt.ToString("dd-HHmmss") + "-" + session.Id[..8]);
        Save(session);
        return session;
    }

    public string Folder(CallSession session) => AppPaths.Within(Root, session.Folder);
    public string TrackFolder(CallSession session, RecordingTrack track) => AppPaths.Within(Folder(session), track.Directory);

    public void Save(CallSession session)
    {
        lock (gate)
        {
            var file = Path.Combine(Folder(session), "session.json");
            AppPaths.AtomicJson(file, session);
            Index(session);
        }
    }

    private void Index(CallSession session)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO sessions VALUES($id,$started,$folder,$json) ON CONFLICT(id) DO UPDATE SET started=$started,folder=$folder,json=$json; DELETE FROM search WHERE id=$id; INSERT INTO search(id,text) VALUES($id,$text);";
        cmd.Parameters.AddWithValue("$id", session.Id);
        cmd.Parameters.AddWithValue("$started", session.StartedAt.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$folder", session.Folder);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(session, AppPaths.Json));
        cmd.Parameters.AddWithValue("$text", string.Join('\n', new[] { session.Title, session.Project, session.Tags, session.Notes }
            .Concat(session.Transcript.Select(x => x.Source).Concat(session.Tracks.Select(x => x.Name)).Distinct())
            .Concat(session.Transcript.Select(x => x.Text)).Concat(session.Bookmarks.Select(x => x.Text))));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public IReadOnlyList<CallSession> Search(string query = "", int offset = 0, int limit = 100)
    {
        var tokens = Words().Matches(query).Select(x => "\"" + x.Value + "\"*").Take(24).ToArray();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = tokens.Length == 0 ? "SELECT json FROM sessions ORDER BY started DESC LIMIT $limit OFFSET $offset"
            : "SELECT s.json FROM sessions s JOIN search f ON s.id=f.id WHERE search MATCH $query ORDER BY s.started DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$query", string.Join(" AND ", tokens));
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader();
        var result = new List<CallSession>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<CallSession>(reader.GetString(0), AppPaths.Json)!);
        return result;
    }

    public CallSession? Get(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM sessions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CallSession>(json, AppPaths.Json) : null;
    }

    /// <summary>Reads the stored session, applies a change and saves it under the archive lock: background work
    /// (compression, transcription) and edits in the window never overwrite each other's fields.</summary>
    public CallSession? Update(string id, Action<CallSession> change)
    {
        lock (gate)
        {
            var session = Get(id);
            if (session is null) return null;
            change(session);
            Save(session);
            return session;
        }
    }

    /// <summary>Projects used before, most recent first — suggestions for the project field.</summary>
    public IReadOnlyList<string> Projects(int limit = 50)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT project FROM (SELECT json_extract(json, '$.Project') AS project, MAX(started) AS last FROM sessions GROUP BY project)
            WHERE project IS NOT NULL AND project <> '' ORDER BY last DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Moves the recording folder to the Windows Recycle Bin and removes it from the index — reversible
    /// from the Recycle Bin, unlike a plain delete.</summary>
    public void Delete(CallSession session, bool toRecycleBin = true)
    {
        if (session.IsRecording) throw new InvalidOperationException("Сначала завершите запись.");
        lock (gate)
        {
            var folder = Folder(session);
            if (Directory.Exists(folder))
            {
                if (toRecycleBin)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else Directory.Delete(folder, true);
            }
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE id=$id; DELETE FROM search WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", session.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Run at start-up. Finishes recordings cut off by a crash or power loss (repairs their WAV headers),
    /// returns interrupted transcriptions to the queue and re-indexes sessions missing from the search index
    /// (a copied or restored archive folder). Sessions that need nothing are not rewritten.</summary>
    public IReadOnlyList<string> Recover()
    {
        var messages = new List<string>();
        var indexed = IndexedIds();
        foreach (var file in Directory.EnumerateFiles(Root, "session.json", SearchOption.AllDirectories))
        {
            try
            {
                var session = JsonSerializer.Deserialize<CallSession>(File.ReadAllText(file), AppPaths.Json)!;
                var folder = Path.GetRelativePath(Root, Path.GetDirectoryName(file)!);
                var changed = !indexed.Contains(session.Id) || session.Folder != folder;
                session.Folder = folder;
                if (session.Status == SessionStatus.Recording)
                {
                    session.Status = SessionStatus.Interrupted;
                    foreach (var track in session.Tracks)
                    {
                        track.Status = "Interrupted";
                        var dir = TrackFolder(session, track);
                        if (Directory.Exists(dir))
                            foreach (var wav in Directory.EnumerateFiles(dir, "*.wav")) PcmTimelineWriter.RepairWave(wav);
                    }
                    messages.Add($"Восстановлена запись после сбоя: {session.Title}");
                    changed = true;
                }
                if (session.TranscriptionStatus == TranscriptionStatus.Running)
                {
                    session.TranscriptionStatus = TranscriptionStatus.Queued;
                    changed = true;
                }
                if (changed) Save(session);
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            { messages.Add($"Не удалось открыть запись {Path.GetFileName(Path.GetDirectoryName(file))}: {e.Message}"); }
        }
        return messages;
    }

    private HashSet<string> IndexedIds()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM sessions";
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Words();
}
