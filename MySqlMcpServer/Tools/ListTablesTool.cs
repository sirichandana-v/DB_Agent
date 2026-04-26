using System.ComponentModel;
using ModelContextProtocol.Server;
using MySqlMcpServer.Logging;
using MySqlMcpServer.Services;

namespace MySqlMcpServer.Tools;

[McpServerToolType]
public sealed class ListTablesTool
{
    private readonly SchemaCache _cache;
    private readonly DailyFileLogger _log;

    public ListTablesTool(SchemaCache cache, DailyFileLogger log)
    {
        _cache = cache;
        _log = log;
    }

    [McpServerTool(Name = "list_tables", ReadOnly = true, Destructive = false)]
    [Description("Lists all tables in the connected MySQL database.")]
    public Task<string> ListTablesAsync(CancellationToken cancellationToken)
    {
        _log.Log("INFO", "Tool list_tables");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_cache.ListTablesCsv());
    }
}
