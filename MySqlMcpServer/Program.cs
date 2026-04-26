using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MySqlMcpServer.Logging;
using MySqlMcpServer.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

var logDirectory = builder.Configuration["Logging:LogDirectory"] ?? "logs";
if (!Path.IsPathRooted(logDirectory))
    logDirectory = Path.Combine(AppContext.BaseDirectory, logDirectory);
var logPrefix = builder.Configuration["Logging:FileNamePrefix"] ?? "mcp-server";

builder.Services.AddSingleton(_ => new DailyFileLogger(logDirectory, logPrefix));
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<SchemaCache>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(Program).Assembly);

var app = builder.Build();

var logger = app.Services.GetRequiredService<DailyFileLogger>();
logger.Log("INFO", "MySql MCP server starting (stdio transport).");

var schemaCache = app.Services.GetRequiredService<SchemaCache>();
await schemaCache.InitializeAsync().ConfigureAwait(false);

logger.Log("INFO", "Schema loaded; ready for MCP requests.");

await app.RunAsync().ConfigureAwait(false);
