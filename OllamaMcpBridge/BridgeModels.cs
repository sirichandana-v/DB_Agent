namespace OllamaMcpBridge;

public sealed class BridgeRunResult
{
    public bool Success { get; init; }

    /// <summary>
    /// Exit codes from <c>RunDirectSqlPipeline</c>: 0 = ok, 1 = missing MySQL config, 2 = Ollama HTTP error, 3 = bad Ollama
    /// response or schema error, 7 = read-only SQL guard, 8 = SQL sanitizer, 9 = execute_query failure.
    /// (Codes 4–6 are unused in the current pipeline; reserved for a future multi-round tool-calling path.)
    /// </summary>
    public int ExitCode { get; init; }

    public string? ErrorDetail { get; init; }

    public string? AssistantFinalText { get; init; }

    public IReadOnlyList<ToolCallRecord> ToolCalls { get; init; } = Array.Empty<ToolCallRecord>();

    /// <summary>Each Ollama /api/chat request: pretty-printed JSON array of messages (roles + content) actually sent.</summary>
    public IReadOnlyList<OllamaChatRequestRecord> OllamaChatRequests { get; init; } = Array.Empty<OllamaChatRequestRecord>();

    /// <summary>Raw <c>get_schema_context</c> text included in the user message to the model.</summary>
    public string? SchemaText { get; init; }

    /// <summary>Full user-role message (schema + question + instructions) sent to the model.</summary>
    public string? UserMessageSent { get; init; }

    /// <summary>Per-attempt log for the direct SQL pipeline: model call and what happened next.</summary>
    public IReadOnlyList<DirectSqlExchange> DirectSqlExchanges { get; init; } = Array.Empty<DirectSqlExchange>();
}

/// <summary>One POST to Ollama: the <c>messages</c> array body field (not the full request).</summary>
public sealed class OllamaChatRequestRecord
{
    public required string Pipeline { get; init; }

    public int? Round { get; init; }

    public required string MessagesJson { get; init; }
}

/// <summary>
/// Traces orchestrator MCP invocations (schema + <c>execute_query</c>) for logging, API, and JSONL. The model
/// does not return tool calls; this is what C# called on the server.
/// </summary>
public sealed class ToolCallRecord
{
    public required string ToolName { get; init; }

    public IReadOnlyDictionary<string, object?> Arguments { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public required string ResultText { get; init; }
}

/// <summary>One try in the direct-SQL flow: Ollama round-trip and parse / guard / execute outcome.</summary>
public sealed class DirectSqlExchange
{
    public int Attempt { get; init; }

    /// <summary>Human-readable step, e.g. <c>ok</c>, <c>sql_sanitize_failed</c>, <c>execute_failed</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>JSON array of messages in that <c>POST /api/chat</c> request (same as trace entry).</summary>
    public string? OllamaMessagesJson { get; init; }

    public string? AssistantRaw { get; init; }

    public string? SanitizedSql { get; init; }

    public string? ErrorOrHint { get; init; }

    /// <summary>Result from <c>execute_query</c> when the attempt reached execution (error JSON or row JSON).</summary>
    public string? ExecuteResultPreview { get; init; }

    /// <summary>Pretty-printed full HTTP body from <c>POST /api/chat</c> (always present when the server returned a body).</summary>
    public string? OllamaFullResponseJson { get; init; }
}
