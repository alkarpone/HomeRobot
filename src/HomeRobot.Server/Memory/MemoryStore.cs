using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HomeRobot.Server.Memory;

public sealed class MemoryOptions
{
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRobot", "memory.sqlite3");
}

public sealed record MemoryEntry(Guid Id, string Content, string Category, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string Source, Guid? SessionId, bool IsActive);

public sealed class MemoryStore(IOptions<MemoryOptions> options, TimeProvider clock)
{
    private const int Capacity = 1000;
    private static readonly string[] Categories = ["personal", "preferences", "people_pets", "projects", "routines", "other"];
    private static readonly Regex Secret = new(
        @"hesl|password|passphrase|api[ _-]?(?:key|klic)|token|private[ _-]?key|tajny[ _-]?klic|(?:sk|ghp|github_pat|xoxb)[_-][a-z0-9_-]{12,}|akia[a-z0-9]{16}|[a-z0-9_+/=-]{32,}",
        RegexOptions.IgnoreCase);

    public static bool ValidCategory(string? category) => Categories.Contains(category);

    public static bool ValidContent(string? content) => content is { Length: > 0 and <= 500 } &&
        content.Any(char.IsLetterOrDigit) && !content.Any(char.IsControl) && !Secret.IsMatch(Fold(content));

    internal static string Fold(string content) => string.Concat(content.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
        .Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private SqliteConnection Open()
    {
        var path = Path.GetFullPath(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, DefaultTimeout = 3, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                PRAGMA secure_delete = ON;
                CREATE TABLE IF NOT EXISTS memory (
                    id TEXT PRIMARY KEY, content TEXT NOT NULL, category TEXT NOT NULL,
                    created TEXT NOT NULL, updated TEXT NOT NULL, source TEXT NOT NULL,
                    session TEXT, normalized TEXT NOT NULL UNIQUE, revision TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS personality (
                    id INTEGER PRIMARY KEY CHECK (id = 1), profile TEXT NOT NULL
                );
                """;
            schema.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal string? ReadPersonality()
    {
        if (!File.Exists(options.Value.DatabasePath))
            return null;
        using var db = Open();
        using var command = Command(db, null, "SELECT profile FROM personality WHERE id = 1");
        return command.ExecuteScalar() as string;
    }

    internal void SavePersonality(string profile)
    {
        using var db = Open();
        using var command = Command(db, null, """
            INSERT INTO personality VALUES (1, $profile)
            ON CONFLICT(id) DO UPDATE SET profile = excluded.profile
            """, ("$profile", profile));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MemoryEntry> List(string? category = null) =>
        Snapshot(category).Select(row => row.Entry).ToArray();

    internal SnapshotRow[] Snapshot(string? category)
    {
        if (!File.Exists(options.Value.DatabasePath))
            return [];
        using var db = Open();
        return Snapshot(db, null, category);
    }

    private static SnapshotRow[] Snapshot(SqliteConnection db, SqliteTransaction? transaction, string? category)
    {
        using var command = Command(db, transaction, """
            SELECT id, content, category, created, updated, source, session, revision FROM memory
            WHERE $category IS NULL OR category = $category ORDER BY updated DESC, id
            """, ("$category", category));
        return ReadRows(command);
    }

    internal IReadOnlyList<MemoryEntry> Search(string? category, Func<string, int> score)
    {
        if (!File.Exists(options.Value.DatabasePath))
            return [];
        using var db = Open();
        db.CreateFunction("keyword_score", score, isDeterministic: true);
        using var command = Command(db, null, """
            SELECT id, content, category, created, updated, source, session, revision
            FROM (
                SELECT *, keyword_score(content) AS score FROM memory
                WHERE $category IS NULL OR category = $category
            )
            WHERE score > 0 ORDER BY score DESC, updated DESC, id LIMIT 3
            """, ("$category", category));
        return ReadRows(command).Select(row => row.Entry).ToArray();
    }

    private static SnapshotRow[] ReadRows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<SnapshotRow>();
        while (reader.Read())
        {
            rows.Add(new(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetString(5),
                reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), true), reader.GetString(7)));
        }
        return rows.ToArray();
    }

    public MemoryEntry? Create(string content, string category, string source, Guid? session = null)
    {
        var now = clock.GetUtcNow();
        var entry = new MemoryEntry(Guid.NewGuid(), content.Trim(), category, now, now, source, session, true);
        using var db = Open();
        using var command = Command(db, null, """
            INSERT INTO memory
            SELECT $id, $content, $category, $now, $now, $source, $session, $normalized, $revision
            WHERE (SELECT count(*) FROM memory) < $capacity
            ON CONFLICT(normalized) DO NOTHING
            """, ("$id", entry.Id.ToString()), ("$content", entry.Content), ("$category", category),
            ("$now", now.ToString("O")), ("$source", source), ("$session", session?.ToString()),
            ("$normalized", Normalize(entry.Content)), ("$capacity", Capacity), ("$revision", Guid.NewGuid().ToString()));
        return command.ExecuteNonQuery() == 1 ? entry : null;
    }

    internal bool Update(Guid id, string content, string category, string? revision = null)
    {
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE OR IGNORE memory SET content = $content, category = $category, updated = $now,
                normalized = $normalized, revision = $next
            WHERE id = $id AND ($revision IS NULL OR revision = $revision)
            """, ("$id", id.ToString()), ("$content", content.Trim()), ("$category", category),
            ("$now", clock.GetUtcNow().ToString("O")), ("$normalized", Normalize(content)),
            ("$revision", revision), ("$next", Guid.NewGuid().ToString()));
        return command.ExecuteNonQuery() == 1;
    }

    internal bool Delete(Guid id, string? revision = null)
    {
        using var db = Open();
        using var command = Command(db, null,
            "DELETE FROM memory WHERE id = $id AND ($revision IS NULL OR revision = $revision)",
            ("$id", id.ToString()), ("$revision", revision));
        return command.ExecuteNonQuery() == 1;
    }

    internal bool DeleteAll(string? category, string? expected = null)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        if (expected is not null && Fingerprint(Snapshot(db, transaction, category)) != expected)
            return false;
        using var command = Command(db, transaction, "DELETE FROM memory WHERE $category IS NULL OR category = $category",
            ("$category", category));
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    internal static string Fingerprint(IEnumerable<SnapshotRow> rows) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', rows.Select(row => row.Revision)))));

    private static string Normalize(string content) => Regex.Replace(Fold(content).Trim().TrimEnd('.', '!', '?'), @"\s+", " ");

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal sealed record SnapshotRow(MemoryEntry Entry, string Revision);
}
