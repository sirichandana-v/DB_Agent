namespace OllamaMcpBridge;

public sealed class BridgeRunResult
{
    public bool Success { get; init; }

    /// <summary>Non-zero exit code style: 0 = ok, 1 config, 2 ollama http, 3 bad response, 4 max iterations, 5 malformed in-message tool JSON, 6 final answer without successful execute_query after failed attempts.</summary>
    public int ExitCode { get; init; }

    public string? ErrorDetail { get; init; }

    public string? AssistantFinalText { get; init; }

    public IReadOnlyList<ToolCallRecord> ToolCalls { get; init; } = Array.Empty<ToolCallRecord>();
}

public sealed class ToolCallRecord
{
    public required string ToolName { get; init; }

    public IReadOnlyDictionary<string, object?> Arguments { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public required string ResultText { get; init; }
}
