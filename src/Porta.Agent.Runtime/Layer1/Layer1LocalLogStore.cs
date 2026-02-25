using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Porta.Agent.Runtime.Layer1;

internal sealed class Layer1LocalLogStore : IDisposable
{
    private const long MaxBytes = 50L * 1024L * 1024L;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly byte[] _encryptionKey;
    private readonly byte[] _signingKey;
    private readonly object _gate = new();

    public Layer1LocalLogStore(string machineId)
    {
        _dbPath = BuildDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = $"Data Source={_dbPath}";
        _encryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes($"porta-local-aes|{machineId}|v1"));
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes($"porta-local-sign|{machineId}|v1"));
        EnsureSchema();
    }

    public double Log(string eventType, object payload)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string json = JsonSerializer.Serialize(payload);
        Encrypt(json, out string cipher, out string nonce, out string tag);
        string signature = Sign(eventType, cipher, nonce, tag);
        lock (_gate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO local_logs (timestamp_utc, event_type, payload_cipher, nonce, tag, signature)
                VALUES ($ts, $et, $pc, $nonce, $tag, $sig);
                """;
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$et", eventType);
            cmd.Parameters.AddWithValue("$pc", cipher);
            cmd.Parameters.AddWithValue("$nonce", nonce);
            cmd.Parameters.AddWithValue("$tag", tag);
            cmd.Parameters.AddWithValue("$sig", signature);
            cmd.ExecuteNonQuery();
            PruneIfNeeded(connection);
        }

        return Math.Max(0, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }

    public string ExportJson()
    {
        lock (_gate)
        {
            string outputPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, $"layer1-logs-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            var rows = ReadRows();
            var docs = rows.Select(row => new
            {
                timestamp = row.TimestampUtc,
                event_type = row.EventType,
                payload = Decrypt(row.PayloadCipher, row.Nonce, row.Tag),
                signature = row.Signature
            });
            File.WriteAllText(outputPath, JsonSerializer.Serialize(docs, new JsonSerializerOptions { WriteIndented = true }));
            return outputPath;
        }
    }

    public string ExportCsv()
    {
        lock (_gate)
        {
            string outputPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, $"layer1-logs-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("timestamp_utc,event_type,payload_json,signature");
            foreach ((string ts, string et, string payloadCipher, string nonce, string tag, string sig) in ReadRows())
            {
                string payload = Decrypt(payloadCipher, nonce, tag).Replace("\"", "\"\"");
                sb.AppendLine($"\"{ts}\",\"{et}\",\"{payload}\",\"{sig}\"");
            }
            File.WriteAllText(outputPath, sb.ToString());
            return outputPath;
        }
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS local_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                payload_cipher TEXT NOT NULL,
                nonce TEXT NOT NULL,
                tag TEXT NOT NULL,
                signature TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_local_logs_ts ON local_logs(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_local_logs_event ON local_logs(event_type);
            """;
        cmd.ExecuteNonQuery();
    }

    private void PruneIfNeeded(SqliteConnection connection)
    {
        using (var retention = connection.CreateCommand())
        {
            retention.CommandText =
                """
                DELETE FROM local_logs
                WHERE timestamp_utc < $cutoff;
                """;
            retention.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToString("O"));
            retention.ExecuteNonQuery();
        }

        FileInfo fi = new(_dbPath);
        if (!fi.Exists || fi.Length <= MaxBytes)
        {
            return;
        }

        using var prune = connection.CreateCommand();
        prune.CommandText =
            """
            DELETE FROM local_logs
            WHERE id IN (
                SELECT id FROM local_logs ORDER BY id ASC LIMIT 1000
            );
            """;
        prune.ExecuteNonQuery();
    }

    private List<(string TimestampUtc, string EventType, string PayloadCipher, string Nonce, string Tag, string Signature)> ReadRows()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT timestamp_utc, event_type, payload_cipher, nonce, tag, signature
            FROM local_logs
            ORDER BY id ASC;
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string, string, string, string)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private void Encrypt(string plaintext, out string cipher, out string nonce, out string tag)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonceBytes = RandomNumberGenerator.GetBytes(12);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tagBytes = new byte[16];
        using var aes = new AesGcm(_encryptionKey, tagSizeInBytes: 16);
        aes.Encrypt(nonceBytes, plainBytes, cipherBytes, tagBytes);
        cipher = Convert.ToBase64String(cipherBytes);
        nonce = Convert.ToBase64String(nonceBytes);
        tag = Convert.ToBase64String(tagBytes);
    }

    private string Decrypt(string cipher, string nonce, string tag)
    {
        byte[] cipherBytes = Convert.FromBase64String(cipher);
        byte[] nonceBytes = Convert.FromBase64String(nonce);
        byte[] tagBytes = Convert.FromBase64String(tag);
        byte[] plainBytes = new byte[cipherBytes.Length];
        using var aes = new AesGcm(_encryptionKey, tagSizeInBytes: 16);
        aes.Decrypt(nonceBytes, cipherBytes, tagBytes, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private string Sign(string eventType, string cipher, string nonce, string tag)
    {
        using var hmac = new HMACSHA256(_signingKey);
        string data = $"{eventType}|{cipher}|{nonce}|{tag}";
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    private static string BuildDbPath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "Porta", "Layer1", "local-logs.db");
    }

    public void Dispose()
    {
    }
}
