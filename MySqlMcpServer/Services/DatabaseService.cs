using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using MySqlMcpServer.Logging;

namespace MySqlMcpServer.Services;

public sealed class DatabaseService
{
    private readonly string _connectionString;
    private readonly DailyFileLogger _log;
    public const int MaxRows = 200;

    public DatabaseService(IConfiguration configuration, DailyFileLogger log)
    {
        _log = log;
        var cs = configuration.GetConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs))
            cs = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(cs))
            cs = Environment.GetEnvironmentVariable("ConnectionStrings__MySQL");
        _connectionString = !string.IsNullOrWhiteSpace(cs)
            ? cs
            : throw new InvalidOperationException(
                "Connection string missing. Set ConnectionStrings:MySQL in appsettings or environment variable ConnectionStrings__MySQL.");
    }

    public async Task<List<string>> GetTablesAsync(CancellationToken cancellationToken = default)
    {
        _log.Log("INFO", "DatabaseService.GetTablesAsync");
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME
            """;
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task<string> DescribeTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        _log.Log("INFO", $"DatabaseService.DescribeTableAsync table={tableName}");
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_KEY
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("table", tableName);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = reader.GetString(0),
                ["type"] = reader.GetString(1),
                ["nullable"] = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                ["key"] = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }

        return JsonSerializer.Serialize(rows);
    }

    public async Task<string> ExecuteQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        _log.Log("INFO", $"DatabaseService.ExecuteQueryAsync SQL: {sql}");
        if (!SqlReadOnlyGuard.IsAllowedReadOnlySql(sql, out var guardError))
            return JsonSerializer.Serialize(new { error = guardError });

        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var fieldCount = reader.FieldCount;
            var names = Enumerable.Range(0, fieldCount).Select(reader.GetName).ToArray();
            var count = 0;
            while (count < MaxRows && await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fieldCount; i++)
                {
                    if (reader.IsDBNull(i))
                        row[names[i]] = null;
                    else
                        row[names[i]] = reader.GetValue(i);
                }

                rows.Add(row);
                count++;
            }

            return JsonSerializer.Serialize(rows);
        }
        catch (Exception ex)
        {
            _log.Log("ERROR", $"ExecuteQueryAsync failed: {ex.Message}");
            return JsonSerializer.Serialize(new { error = "Query failed.", detail = ex.Message });
        }
    }
}
