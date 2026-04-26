using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OllamaMcpBridge;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var app = builder.Build();

// Chrome DevTools probes this optional URL; without a route it 404s and clutters logs.
app.MapGet("/.well-known/appspecific/com.chrome.devtools.json", () => Results.Json(new { }));

app.UseDefaultFiles();
app.UseStaticFiles();

// CamelCase so the static UI (data.success, data.toolCalls) matches serialized names. Without this,
// `new { result.Success }` becomes JSON property "Success" and `data.success` is undefined → "Run failed".
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

app.MapPost("/api/ask", async (AskRequest req, IConfiguration config, ILoggerFactory lf, CancellationToken ct) =>
{
    var q = req.Question?.Trim();
    if (string.IsNullOrEmpty(q))
        return Results.BadRequest(new { error = "question is required" });

    try
    {
        var result = await BridgeRunner.RunAsync(q, config, lf, null, null, ct).ConfigureAwait(false);
        var toolCalls = result.ToolCalls.Select(tc => new
        {
            toolName = tc.ToolName,
            arguments = tc.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
            result = tc.ResultText,
            resultText = tc.ResultText
        }).ToList();

        var ollamaChatRequests = result.OllamaChatRequests.Select(r => new
        {
            r.Pipeline,
            r.Round,
            messagesJson = r.MessagesJson
        }).ToList();

        var directSqlExchanges = result.DirectSqlExchanges.Select(e => new
        {
            e.Attempt,
            e.Outcome,
            ollamaMessagesJson = e.OllamaMessagesJson,
            ollamaFullResponseJson = e.OllamaFullResponseJson,
            e.AssistantRaw,
            e.SanitizedSql,
            e.ErrorOrHint,
            e.ExecuteResultPreview
        }).ToList();

        return Results.Json(new
        {
            success = result.Success,
            exitCode = result.ExitCode,
            error = result.ErrorDetail,
            assistantReply = result.AssistantFinalText,
            toolCalls,
            ollamaChatRequests,
            schemaText = result.SchemaText,
            directSqlExchanges
        }, jsonOptions);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.ToString(), statusCode: 500);
    }
});

app.Run();

file sealed class AskRequest
{
    [JsonPropertyName("question")]
    public string? Question { get; set; }
}
