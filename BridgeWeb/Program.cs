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
        var result = await BridgeRunner.RunAsync(q, config, lf, ct).ConfigureAwait(false);
        var toolCalls = result.ToolCalls.Select(tc => new
        {
            toolName = tc.ToolName,
            arguments = tc.Arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
            // Primary field for the trace UI.
            result = tc.ResultText,
            // Older cached index.html read resultText / resultJson; keep aliases so Result is never blank after API changes.
            resultText = tc.ResultText
        }).ToList();

        return Results.Json(new
        {
            success = result.Success,
            exitCode = result.ExitCode,
            error = result.ErrorDetail,
            assistantReply = result.AssistantFinalText,
            toolCalls
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
