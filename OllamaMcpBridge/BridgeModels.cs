namespace OllamaMcpBridge;

public sealed class BridgeRunResult
{
    public bool Success { get; init; }

    /// <summary>Non-zero exit code style: 0 = ok, 1 config, 2 ollama http, 3 bad response, 4 max iterations, 5 malformed in-message tool JSON, 6 final answer without successful execute_query after failed attempts, 7 SqlReadOnlyGuard, 8 SqlSanitizer/parse, 9 execute_query returned DB error JSON.</summary>
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
