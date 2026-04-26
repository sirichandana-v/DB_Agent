using System.ComponentModel;
using ModelContextProtocol.Server;
using MySqlMcpServer.Logging;
using MySqlMcpServer.Services;

namespace MySqlMcpServer.Tools;

[McpServerToolType]
public sealed class GetSchemaContextTool
{
    private readonly SchemaCache _cache;
    private readonly DailyFileLogger _log;

    public GetSchemaContextTool(SchemaCache cache, DailyFileLogger log)
    {
        _cache = cache;
        _log = log;
    }

    [McpServerTool(Name = "get_schema_context", ReadOnly = true, Destructive = false)]
    [Description(
        "Returns ALL tables and ALL their columns in one call. Always call this first. Use only names from this text in SQL—if a name is not listed, it does not exist (do not substitute singular/plural guesses like employee vs employees).")]
    public Task<string> GetSchemaContextAsync(CancellationToken cancellationToken)
    {
        _log.Log("INFO", "Tool get_schema_context");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_cache.GetFullSchemaContext());
    }
}
