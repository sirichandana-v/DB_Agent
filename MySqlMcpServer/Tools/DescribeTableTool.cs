using System.ComponentModel;
using ModelContextProtocol.Server;
using MySqlMcpServer.Logging;
using MySqlMcpServer.Services;

namespace MySqlMcpServer.Tools;

[McpServerToolType]
public sealed class DescribeTableTool
{
    private readonly SchemaCache _cache;
    private readonly DailyFileLogger _log;

    public DescribeTableTool(SchemaCache cache, DailyFileLogger log)
    {
        _cache = cache;
        _log = log;
    }

    [McpServerTool(Name = "describe_table", ReadOnly = true, Destructive = false)]
    [Description("Returns columns and types for a specific table.")]
    public Task<string> DescribeTableAsync(
        [Description("MySQL table name")] string table_name,
        CancellationToken cancellationToken)
    {
        _log.Log("INFO", $"Tool describe_table table_name={table_name}");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_cache.DescribeTableJson(table_name));
    }
}
