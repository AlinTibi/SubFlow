using System.Text.Json;
using System.Text.Json.Serialization;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Translation;
using Microsoft.Data.Sqlite;

namespace ALMARFELD.SubFlow.Infrastructure.Persistence;

/// <summary>SQLite database holding the job queue/history and the translation cache.</summary>
public sealed class SqliteStore : IJobStore, ITranslationCache
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public SqliteStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Initialise();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Initialise()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS job_items (
                id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL,
                source_path TEXT NOT NULL,
                status TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                data TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_job_items_status ON job_items(status);
            CREATE TABLE IF NOT EXISTS translation_cache (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            INSERT INTO schema_info(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ jobs

    public void Upsert(JobItem item)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO job_items(id, batch_id, source_path, status, created_utc, updated_utc, data)
            VALUES ($id, $batch, $src, $status, $created, $updated, $data)
            ON CONFLICT(id) DO UPDATE SET status = excluded.status, updated_utc = excluded.updated_utc, data = excluded.data;
            """;
        cmd.Parameters.AddWithValue("$id", item.Id.ToString());
        cmd.Parameters.AddWithValue("$batch", item.BatchId.ToString());
        cmd.Parameters.AddWithValue("$src", item.SourcePath);
        cmd.Parameters.AddWithValue("$status", item.Status.ToString());
        cmd.Parameters.AddWithValue("$created", item.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(item, Json));
        cmd.ExecuteNonQuery();
    }

    public void Delete(Guid id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM job_items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public JobItem? Get(Guid id) =>
        Query("SELECT data FROM job_items WHERE id = $id", ("$id", id.ToString())).FirstOrDefault();

    public IReadOnlyList<JobItem> GetQueue() =>
        Query("SELECT data FROM job_items WHERE status IN ('Pending','Running','Interrupted','Cancelled') ORDER BY created_utc, source_path");

    public IReadOnlyList<JobItem> GetHistory(int limit = 500) =>
        Query("SELECT data FROM job_items WHERE status IN ('Completed','CompletedWithWarnings','Skipped','Failed','Cancelled') ORDER BY updated_utc DESC LIMIT $limit",
            ("$limit", limit));

    public int MarkRunningAsInterrupted()
    {
        var running = Query("SELECT data FROM job_items WHERE status = 'Running'");
        foreach (var item in running)
        {
            item.Status = JobStatus.Interrupted;
            item.Message = "Interrupted when the application closed. Resume to continue.";
            Upsert(item);
        }
        return running.Count;
    }

    public void ClearHistory()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM job_items WHERE status IN ('Completed','CompletedWithWarnings','Skipped','Failed','Cancelled')";
        cmd.ExecuteNonQuery();
    }

    private List<JobItem> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<JobItem>();
        while (r.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<JobItem>(r.GetString(0), Json) is { } item) list.Add(item);
            }
            catch (JsonException) { /* skip rows written by an incompatible version */ }
        }
        return list;
    }

    // ------------------------------------------------------------------ translation cache

    public IReadOnlyDictionary<string, string> GetMany(IEnumerable<string> keys)
    {
        var result = new Dictionary<string, string>();
        var list = keys.Distinct().ToList();
        if (list.Count == 0) return result;
        using var c = Open();
        foreach (var chunk in list.Chunk(400))
        {
            using var cmd = c.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                names.Add($"$k{i}");
                cmd.Parameters.AddWithValue($"$k{i}", chunk[i]);
            }
            cmd.CommandText = $"SELECT key, value FROM translation_cache WHERE key IN ({string.Join(",", names)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.GetString(1);
        }
        return result;
    }

    public void SetMany(IEnumerable<KeyValuePair<string, string>> entries)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO translation_cache(key, value, created_utc) VALUES ($k, $v, $t)";
        var k = cmd.Parameters.Add("$k", SqliteType.Text);
        var v = cmd.Parameters.Add("$v", SqliteType.Text);
        var t = cmd.Parameters.Add("$t", SqliteType.Text);
        var now = DateTime.UtcNow.ToString("O");
        foreach (var (key, value) in entries)
        {
            k.Value = key;
            v.Value = value;
            t.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public long Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM translation_cache";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void Clear()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM translation_cache";
        cmd.ExecuteNonQuery();
    }
}
