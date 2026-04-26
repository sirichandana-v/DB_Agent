using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OllamaMcpBridge;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var emitToolJsonl = configuration.GetValue("Bridge:EmitToolJsonl", false)
    || string.Equals(Environment.GetEnvironmentVariable("OLLAMA_BRIDGE_EMIT_TOOL_JSONL"), "1", StringComparison.Ordinal);

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: OllamaMcpBridge \"your natural language question\"");
    Console.Error.WriteLine("Example: OllamaMcpBridge \"How many rows are in the users table?\"");
    Console.Error.WriteLine("Machine-readable tool I/O (stdout, one JSON object per tool call): set OLLAMA_BRIDGE_EMIT_TOOL_JSONL=1");
    return 1;
}

var userMessage = string.Join(" ", args);

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Information);
    b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
});

var result = await BridgeRunner.RunAsync(userMessage, configuration, loggerFactory).ConfigureAwait(false);
if (!result.Success)
{
    Console.Error.WriteLine(result.ErrorDetail);
    return result.ExitCode;
}

if (emitToolJsonl)
{
    foreach (var tc in result.ToolCalls)
        BridgeRunner.EmitToolJsonlLine(true, tc.ToolName, tc.Arguments, tc.ResultText);
}
else
{
    Console.WriteLine(result.AssistantFinalText);
}

return 0;
