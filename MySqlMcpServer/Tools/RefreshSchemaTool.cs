using System.ComponentModel;
using ModelContextProtocol.Server;
using MySqlMcpServer.Logging;
using MySqlMcpServer.Services;

namespace MySqlMcpServer.Tools;

[McpServerToolType]
public sealed class RefreshSchemaTool
{
    private readonly SchemaCache _cache;
    private readonly DailyFileLogger _log;

    public RefreshSchemaTool(SchemaCache cache, DailyFileLogger log)
    {
        _cache = cache;
        _log = log;
    }

    [McpServerTool(Name = "refresh_schema", ReadOnly = true, Destructive = false)]
    [Description("Reloads the cached schema from MySQL after DDL changes (new tables/columns). Call when the database structure may have changed.")]
    public async Task<string> RefreshSchemaAsync(CancellationToken cancellationToken)
    {
        _log.Log("INFO", "Tool refresh_schema");
        await _cache.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return "Schema cache refreshed. Use get_schema_context for the updated layout.";
    }
}
