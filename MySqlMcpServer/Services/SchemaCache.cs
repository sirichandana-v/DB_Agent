using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MySqlMcpServer.Logging;

namespace MySqlMcpServer.Services;

public sealed class SchemaCache
{
    private readonly DatabaseService _db;
    private readonly DailyFileLogger _log;
    private readonly int _startupRetries;
    private readonly int _retryDelayMs;
    private readonly object _gate = new();
    private List<string> _tableOrder = new();
    private Dictionary<string, List<ColumnDef>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private string _fullContextText = "";

    public SchemaCache(DatabaseService db, IConfiguration configuration, DailyFileLogger log)
    {
        _db = db;
        _log = log;
        _startupRetries = configuration.GetValue("SchemaCache:StartupRetries", 3);
        _retryDelayMs = configuration.GetValue("SchemaCache:RetryDelayMs", 2000);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= _startupRetries; attempt++)
        {
            try
            {
                await ReloadCoreAsync(cancellationToken).ConfigureAwait(false);
                _log.Log("INFO", "SchemaCache initialized successfully.");
                return;
            }
            catch (Exception ex) when (attempt < _startupRetries)
            {
                _log.Log("WARN", $"SchemaCache init attempt {attempt}/{_startupRetries} failed: {ex.Message}. Retrying in {_retryDelayMs}ms.");
                await Task.Delay(_retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Log("ERROR", $"SchemaCache init failed after {_startupRetries} attempts: {ex.Message}");
                throw;
            }
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _log.Log("INFO", "SchemaCache.RefreshAsync invoked.");
        return ReloadCoreAsync(cancellationToken);
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        var tables = await _db.GetTablesAsync(cancellationToken).ConfigureAwait(false);
        var colMap = new Dictionary<string, List<ColumnDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            var json = await _db.DescribeTableAsync(t, cancellationToken).ConfigureAwait(false);
            var cols = ParseColumns(json);
            colMap[t] = cols;
        }

        var sb = new StringBuilder();
        foreach (var t in tables)
        {
            sb.AppendLine($"Table: {t}");
            foreach (var c in colMap[t])
                sb.AppendLine($"  - {c.Name} ({c.Type}) nullable={c.Nullable} key={c.Key}");
            sb.AppendLine();
        }

        lock (_gate)
        {
            _tableOrder = tables;
            _columns = colMap;
            _fullContextText = sb.ToString().TrimEnd();
        }
    }

    private static List<ColumnDef> ParseColumns(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ColumnDef>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new ColumnDef(
                el.GetProperty("name").GetString() ?? "",
                el.GetProperty("type").GetString() ?? "",
                el.GetProperty("nullable").GetBoolean(),
                el.GetProperty("key").GetString() ?? ""));
        }

        return list;
    }

    public string GetFullSchemaContext()
    {
        lock (_gate)
            return _fullContextText;
    }

    public string ListTablesCsv()
    {
        lock (_gate)
            return string.Join(", ", _tableOrder);
    }

    public string DescribeTableJson(string tableName)
    {
        lock (_gate)
        {
            if (!_columns.TryGetValue(tableName, out var cols))
                return JsonSerializer.Serialize(new { error = $"Unknown table: {tableName}" });

            var payload = cols.Select(c => new
            {
                name = c.Name,
                type = c.Type,
                nullable = c.Nullable,
                key = c.Key
            });
            return JsonSerializer.Serialize(payload);
        }
    }

    private readonly record struct ColumnDef(string Name, string Type, bool Nullable, string Key);
}
