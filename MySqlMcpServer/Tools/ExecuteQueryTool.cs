using System.ComponentModel;
using ModelContextProtocol.Server;
using MySqlMcpServer.Logging;
using MySqlMcpServer.Services;

namespace MySqlMcpServer.Tools;

[McpServerToolType]
public sealed class ExecuteQueryTool
{
    private readonly DatabaseService _db;
    private readonly DailyFileLogger _log;

    public ExecuteQueryTool(DatabaseService db, DailyFileLogger log)
    {
        _db = db;
        _log = log;
    }

    [McpServerTool(Name = "execute_query", ReadOnly = true, Destructive = false)]
    [Description(
        "Executes a read-only SELECT or WITH (CTE) query against MySQL and returns results as JSON. Call get_schema_context first and copy identifiers exactly. Link employees to projects via assignments, not a fictional employee_project table.")]
    public async Task<string> ExecuteQueryAsync(
        [Description("The SELECT or WITH ... SELECT SQL query")] string sql,
        CancellationToken cancellationToken)
    {
        _log.Log("INFO", $"Tool execute_query sql={sql}");
        return await _db.ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);
    }
}
