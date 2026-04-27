using System.Collections;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MySqlMcpServer;

namespace OllamaMcpBridge;

public static class BridgeRunner
{
    public static async Task<BridgeRunResult> RunAsync(
        string userMessage,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        string? systemPromptOverride = null,
        string? userMessageTemplate = null,
        CancellationToken cancellationToken = default)
    {
        var mysqlConnectionString = configuration.GetConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
            mysqlConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MySQL");
        if (string.IsNullOrWhiteSpace(mysqlConnectionString))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 1,
                ErrorDetail = "Set ConnectionStrings__MySQL in the environment (full connection string for the MCP server)."
            };
        }

        var log = loggerFactory.CreateLogger(nameof(BridgeRunner));

        var projectPath = ResolveMcpProjectPath(configuration);
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var command = configuration["McpServer:Command"] ?? "dotnet";
        var extraArgs = configuration.GetSection("McpServer:ExtraArguments").Get<string[]>() ?? [];

        string[] runArgs;
        var dllDebug = Path.Combine(projectDir, "bin", "Debug", "net8.0", "MySqlMcpServer.dll");
        var dllRelease = Path.Combine(projectDir, "bin", "Release", "net8.0", "MySqlMcpServer.dll");
        if (File.Exists(dllDebug))
            runArgs = new[] { "exec", dllDebug };
        else if (File.Exists(dllRelease))
            runArgs = new[] { "exec", dllRelease };
        else
        {
            var list = new List<string> { "run", "--project", projectPath };
            list.AddRange(extraArgs);
            runArgs = list.ToArray();
        }

        var transportOptions = new StdioClientTransportOptions
        {
            Command = command,
            Arguments = runArgs.ToArray(),
            WorkingDirectory = projectDir,
            Name = "mysql-mcp"
        };

        var childEnv = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is not null)
                childEnv[key] = Convert.ToString(entry.Value);
        }

        childEnv["ConnectionStrings__MySQL"] = mysqlConnectionString;
        childEnv["MYSQL_CONNECTION_STRING"] = mysqlConnectionString;
        transportOptions.EnvironmentVariables = childEnv;

        var transport = new StdioClientTransport(transportOptions, loggerFactory);
        await using var mcp = await McpClient.CreateAsync(transport, new McpClientOptions(), loggerFactory, cancellationToken)
            .ConfigureAwait(false);

        var ollamaChatRequests = new List<OllamaChatRequestRecord>();

        return await RunDirectSqlPipeline(
                userMessage,
                configuration,
                loggerFactory,
                mcp,
                ollamaChatRequests,
                cancellationToken,
                systemPromptOverride,
                userMessageTemplate)
            .ConfigureAwait(false);
    }

    private static async Task<BridgeRunResult> RunDirectSqlPipeline(
        string userMessage,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        McpClient mcp,
        List<OllamaChatRequestRecord> ollamaChatRequests,
        CancellationToken cancellationToken,
        string? systemPromptOverride,
        string? userMessageTemplate)
    {
        var log = loggerFactory.CreateLogger(nameof(BridgeRunner));
        var ollamaBase = configuration["Ollama:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:11434";
        var model = configuration["Ollama:Model"] ?? "qwen2.5-coder:7b";
        var systemPrompt = !string.IsNullOrWhiteSpace(systemPromptOverride)
            ? systemPromptOverride!.Trim()
            : (configuration["Agent:SqlJsonSystemPrompt"] ?? DefaultSqlJsonSystemPrompt);

        var mcpTools = await mcp.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var validToolNames = mcpTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var toolCalls = new List<ToolCallRecord>();
        const int round = 0;
        var exchanges = new List<DirectSqlExchange>();

        var schemaArgs = new Dictionary<string, object?>();
        var schemaText = await ExecuteToolCallAsync(mcp, validToolNames, "get_schema_context", schemaArgs, cancellationToken)
            .ConfigureAwait(false);
        toolCalls.Add(new ToolCallRecord { ToolName = "get_schema_context", Arguments = schemaArgs, ResultText = schemaText });
        LogToolCompletion(log, round, "get_schema_context", schemaArgs, schemaText);

        if (TryGetToolErrorDetail(schemaText, out var schemaErr))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 3,
                ErrorDetail = "get_schema_context failed: " + schemaErr,
                ToolCalls = toolCalls,
                OllamaChatRequests = ollamaChatRequests,
                SchemaText = schemaText
            };
        }

        if (string.IsNullOrWhiteSpace(schemaText))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 3,
                ErrorDetail = "get_schema_context returned empty schema.",
                ToolCalls = toolCalls,
                OllamaChatRequests = ollamaChatRequests
            };
        }

        var userContent = BuildUserMessageForLlm(userMessage, schemaText, userMessageTemplate);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = userContent }
        };

        const int maxDirectSqlAttempts = 2;
        string? lastAssistantText = null;
        string? lastErrorDetail = null;

        var ollamaTimeout = configuration.GetValue("Ollama:RequestTimeoutSeconds", 600);
        if (ollamaTimeout < 10)
            ollamaTimeout = 10;
        using var http = new HttpClient { BaseAddress = new Uri(ollamaBase + "/") };
        http.Timeout = TimeSpan.FromSeconds(ollamaTimeout);

        for (var attempt = 1; attempt <= maxDirectSqlAttempts; attempt++)
        {
            if (attempt > 1)
            {
                log.LogWarning(
                    "[direct-sql] Retry attempt {Attempt} of {Max} (identical messages; failure not sent to model). Last error: {Err}",
                    attempt, maxDirectSqlAttempts, lastErrorDetail);
            }

            var requestBody = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNode.Parse(messages.ToJsonString())!,
                ["stream"] = false
            };
            ApplyOllamaChatOptions(configuration, requestBody);

            var requestJson = requestBody.ToJsonString();
            LogOllamaOutgoingRequest(configuration, log, "direct-sql", attempt, model, messages, requestBody, requestJson);
            AppendOllamaChatTrace(ollamaChatRequests, "direct-sql", attempt, messages);

            using var chatContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            chatContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var messagesSnapshot = messages.ToJsonString(OllamaTraceMessagesJsonOptions);
            using var response = await http.PostAsync("api/chat", chatContent, cancellationToken).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var ollamaBodyPretty = PrettyPrintJsonIfPossible(responseText);
            if (!response.IsSuccessStatusCode)
            {
                exchanges.Add(new DirectSqlExchange
                {
                    Attempt = attempt,
                    Outcome = "ollama_http_error",
                    OllamaMessagesJson = messagesSnapshot,
                    OllamaFullResponseJson = ollamaBodyPretty,
                    ErrorOrHint = $"HTTP {(int)response.StatusCode}: {responseText}"
                });
                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 2,
                    ErrorDetail = $"Ollama HTTP {(int)response.StatusCode}: {responseText}",
                    ToolCalls = toolCalls,
                    OllamaChatRequests = ollamaChatRequests,
                    SchemaText = schemaText,
                    UserMessageSent = userContent,
                    DirectSqlExchanges = exchanges
                };
            }

            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("message", out var message))
            {
                exchanges.Add(new DirectSqlExchange
                {
                    Attempt = attempt,
                    Outcome = "ollama_bad_response",
                    OllamaMessagesJson = messagesSnapshot,
                    OllamaFullResponseJson = ollamaBodyPretty,
                    ErrorOrHint = "Response had no 'message' field."
                });
                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 3,
                    ErrorDetail = "Ollama response missing message.\n" + responseText,
                    ToolCalls = toolCalls,
                    OllamaChatRequests = ollamaChatRequests,
                    SchemaText = schemaText,
                    UserMessageSent = userContent,
                    DirectSqlExchanges = exchanges
                };
            }

            var assistantText = ExtractAssistantText(message);
            lastAssistantText = assistantText;
            log.LogInformation(
                "[direct-sql] Raw model message (attempt {Attempt}): {Text}",
                attempt, TruncateForLog(assistantText, 8000));

            var sanitized = SqlSanitizer.Sanitize(assistantText, log);
            if (sanitized is not { Success: true, Sql: var sql } || string.IsNullOrWhiteSpace(sql))
            {
                var err = sanitized.Error ?? "Sanitization failed.";
                lastErrorDetail = err + "\nRaw model output: " + assistantText;
                log.LogWarning(
                    "[direct-sql] Sanitization failed on attempt {Attempt}: {Detail}",
                    attempt, err);
                exchanges.Add(new DirectSqlExchange
                {
                    Attempt = attempt,
                    Outcome = "sql_sanitize_failed",
                    OllamaMessagesJson = messagesSnapshot,
                    OllamaFullResponseJson = ollamaBodyPretty,
                    AssistantRaw = assistantText,
                    ErrorOrHint = err
                });
                if (attempt < maxDirectSqlAttempts)
                    continue;
                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 8,
                    ErrorDetail = lastErrorDetail,
                    AssistantFinalText = lastAssistantText,
                    ToolCalls = toolCalls,
                    OllamaChatRequests = ollamaChatRequests,
                    SchemaText = schemaText,
                    UserMessageSent = userContent,
                    DirectSqlExchanges = exchanges
                };
            }

            log.LogInformation(
                "[direct-sql] Sanitized SQL (attempt {Attempt}): {Sql}",
                attempt, TruncateForLog(sql, 2000));

            if (!SqlReadOnlyGuard.IsAllowedReadOnlySql(sql, out var guardErr))
            {
                lastErrorDetail = (guardErr ?? "SQL failed read-only guard.") + "\nSQL: " + sql;
                log.LogWarning(
                    "[direct-sql] Read-only guard failed on attempt {Attempt}: {Err}",
                    attempt, guardErr);
                exchanges.Add(new DirectSqlExchange
                {
                    Attempt = attempt,
                    Outcome = "read_only_guard_failed",
                    OllamaMessagesJson = messagesSnapshot,
                    OllamaFullResponseJson = ollamaBodyPretty,
                    AssistantRaw = assistantText,
                    SanitizedSql = sql,
                    ErrorOrHint = guardErr
                });
                if (attempt < maxDirectSqlAttempts)
                    continue;
                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 7,
                    ErrorDetail = lastErrorDetail,
                    AssistantFinalText = lastAssistantText,
                    ToolCalls = toolCalls,
                    OllamaChatRequests = ollamaChatRequests,
                    SchemaText = schemaText,
                    UserMessageSent = userContent,
                    DirectSqlExchanges = exchanges
                };
            }

            var execArgs = new Dictionary<string, object?> { ["sql"] = sql };
            var execResult = await ExecuteToolCallAsync(mcp, validToolNames, "execute_query", execArgs, cancellationToken)
                .ConfigureAwait(false);
            toolCalls.Add(new ToolCallRecord { ToolName = "execute_query", Arguments = execArgs, ResultText = execResult });
            LogToolCompletion(log, round, "execute_query", execArgs, execResult);

            if (IsExecuteQueryFailureResult(execResult))
            {
                lastErrorDetail = "execute_query failed or returned an error object.\n" + execResult;
                log.LogWarning(
                    "[direct-sql] execute_query failure on attempt {Attempt}: {Result}",
                    attempt, TruncateForLog(execResult, 2000));
                exchanges.Add(new DirectSqlExchange
                {
                    Attempt = attempt,
                    Outcome = "execute_failed",
                    OllamaMessagesJson = messagesSnapshot,
                    OllamaFullResponseJson = ollamaBodyPretty,
                    AssistantRaw = assistantText,
                    SanitizedSql = sql,
                    ErrorOrHint = "Database returned an error or non-row payload.",
                    ExecuteResultPreview = execResult
                });
                if (attempt < maxDirectSqlAttempts)
                {
                    toolCalls.RemoveAt(toolCalls.Count - 1);
                    continue;
                }
                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 9,
                    ErrorDetail = lastErrorDetail,
                    AssistantFinalText = lastAssistantText,
                    ToolCalls = toolCalls,
                    OllamaChatRequests = ollamaChatRequests,
                    SchemaText = schemaText,
                    UserMessageSent = userContent,
                    DirectSqlExchanges = exchanges
                };
            }

            exchanges.Add(new DirectSqlExchange
            {
                Attempt = attempt,
                Outcome = "ok",
                OllamaMessagesJson = messagesSnapshot,
                OllamaFullResponseJson = ollamaBodyPretty,
                AssistantRaw = assistantText,
                SanitizedSql = sql,
                ExecuteResultPreview = execResult
            });
            return new BridgeRunResult
            {
                Success = true,
                ExitCode = 0,
                AssistantFinalText = execResult,
                ToolCalls = toolCalls,
                OllamaChatRequests = ollamaChatRequests,
                SchemaText = schemaText,
                UserMessageSent = userContent,
                DirectSqlExchanges = exchanges
            };
        }

        throw new UnreachableException("direct-sql pipeline");
    }

    /// <summary>Fallback when <c>Agent:SqlJsonSystemPrompt</c> is unset (also used by BridgeWeb defaults API).</summary>
    public const string DefaultSqlJsonSystemPrompt =
        "You are a MySQL expert. Write only a valid MySQL SELECT query. " +
        "No explanation, no markdown, no JSON wrapper. " +
        "Use only identifiers that appear in the schema block in the user message.";

    /// <summary>Default user message: <c>{schema}</c> and <c>{question}</c> are substituted.</summary>
    public const string DefaultUserMessageTemplate =
        "## Database schema (use only these identifiers verbatim)\r\n" +
        "{schema}\r\n" +
        "\r\n" +
        "## Question\r\n" +
        "{question}\r\n" +
        "\r\n" +
        "Output exactly one read-only MySQL statement: a single SELECT or WITH ... SELECT.";

    private static string BuildUserMessageForLlm(string userQuestion, string schemaText, string? template)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultUserMessageTemplate : template;
        return t
            .Replace("{schema}", schemaText.Trim(), StringComparison.Ordinal)
            .Replace("{question}", userQuestion.Trim(), StringComparison.Ordinal);
    }

    /// <summary>True when the tool returned a JSON object with an "error" property (MCP guard / unknown tool / etc.).</summary>
    private static bool TryGetToolErrorDetail(string? resultText, out string detail)
    {
        detail = "";
        if (string.IsNullOrWhiteSpace(resultText))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(resultText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errEl))
                return false;
            detail = errEl.GetString() ?? "error";
            if (doc.RootElement.TryGetProperty("detail", out var dEl) && dEl.ValueKind == JsonValueKind.String)
                detail = string.IsNullOrWhiteSpace(detail) ? dEl.GetString()! : detail + " " + dEl.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Stdout: one JSON object per MCP tool invocation (for E2E validation of SQL and row data).</summary>
    public static void EmitToolJsonlLine(bool enabled, string toolName, IReadOnlyDictionary<string, object?> args,
        string resultText)
    {
        if (!enabled)
            return;
        var line = new JsonObject { ["tool"] = toolName };
        var argNode = new JsonObject();
        foreach (var kv in args)
            argNode[kv.Key] = ArgToJsonNode(kv.Value);
        line["arguments"] = argNode;
        try
        {
            line["data"] = JsonNode.Parse(resultText)!;
        }
        catch (JsonException)
        {
            line["data"] = resultText;
        }

        Console.WriteLine(line.ToJsonString());
    }

    private static void ApplyOllamaChatOptions(IConfiguration configuration, JsonObject requestBody)
    {
        var options = new JsonObject();
        var temperature = configuration.GetValue<double?>("Ollama:Temperature");
        if (temperature is not null)
            options["temperature"] = temperature.Value;
        var topP = configuration.GetValue<double?>("Ollama:TopP");
        if (topP is not null)
            options["top_p"] = topP.Value;
        var numPredict = configuration.GetValue<int?>("Ollama:NumPredict");
        if (numPredict is not null)
            options["num_predict"] = numPredict.Value;
        if (options.Count > 0)
            requestBody["options"] = options;
    }

    /// <summary>
    /// Logs chat payload sizes (for context-window planning) and optionally each message JSON as sent to Ollama.
    /// Controlled by <c>Bridge:LogOllamaContext</c> and <c>Bridge:LogOllamaFullMessages</c>.
    /// </summary>
    private static void LogOllamaOutgoingRequest(
        IConfiguration configuration,
        ILogger log,
        string pipeline,
        int? round,
        string model,
        JsonArray messages,
        JsonObject requestBody,
        string requestJson)
    {
        if (!configuration.GetValue("Bridge:LogOllamaContext", true))
            return;

        var logFullMessages = configuration.GetValue("Bridge:LogOllamaFullMessages", true);
        var messagesJson = messages.ToJsonString();
        var messagesUtf8 = Encoding.UTF8.GetByteCount(messagesJson);
        var messagesChars = messagesJson.Length;
        var fullRequestUtf8 = Encoding.UTF8.GetByteCount(requestJson);

        var toolsUtf8 = 0;
        string? toolsJson = null;
        if (requestBody["tools"] is { } toolsNode)
        {
            toolsJson = toolsNode.ToJsonString();
            toolsUtf8 = Encoding.UTF8.GetByteCount(toolsJson);
        }

        var optionsUtf8 = 0;
        if (requestBody["options"] is { } optNode)
            optionsUtf8 = Encoding.UTF8.GetByteCount(optNode.ToJsonString());

        var roundLabel = round.HasValue ? round.Value.ToString() : "n/a";

        // ~4 UTF-8 bytes per token is a coarse heuristic (English-ish text); use for rough window planning only.
        log.LogInformation(
            "[ollama-chat] summary pipeline={Pipeline} round={Round} model={Model} messageCount={MessageCount} " +
            "messagesChars={MessagesChars} messagesUtf8Bytes={MessagesUtf8} toolsUtf8Bytes={ToolsUtf8} optionsUtf8Bytes={OptionsUtf8} " +
            "fullRequestUtf8Bytes={FullRequestUtf8} roughTokenEstimateFromFullRequest={RoughTokens}",
            pipeline,
            roundLabel,
            model,
            messages.Count,
            messagesChars,
            messagesUtf8,
            toolsUtf8,
            optionsUtf8,
            fullRequestUtf8,
            fullRequestUtf8 / 4);

        if (!logFullMessages)
            return;

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject msgObj)
                continue;
            var role = msgObj["role"] is JsonValue rv ? rv.GetValue<string>() : msgObj["role"]?.ToString() ?? "?";
            var one = msgObj.ToJsonString();
            var oneUtf8 = Encoding.UTF8.GetByteCount(one);
            log.LogInformation(
                "[ollama-chat] message pipeline={Pipeline} round={Round} index={Index} role={Role} messageUtf8Bytes={Utf8} json={MessageJson}",
                pipeline,
                roundLabel,
                i,
                role,
                oneUtf8,
                one);
        }

        if (toolsJson is not null)
        {
            log.LogInformation(
                "[ollama-chat] tools pipeline={Pipeline} round={Round} toolsUtf8Bytes={Utf8} json={ToolsJson}",
                pipeline,
                roundLabel,
                toolsUtf8,
                toolsJson);
        }
    }

    private static readonly JsonSerializerOptions OllamaTraceMessagesJsonOptions = new() { WriteIndented = true };

    private static void AppendOllamaChatTrace(
        ICollection<OllamaChatRequestRecord> trace,
        string pipeline,
        int? round,
        JsonArray messages)
    {
        // Same JSON tree as in the request body’s "messages" field (full cumulative history for this POST).
        trace.Add(new OllamaChatRequestRecord
        {
            Pipeline = pipeline,
            Round = round,
            MessagesJson = messages.ToJsonString(OllamaTraceMessagesJsonOptions)
        });
    }

    /// <summary>
    /// Runs an MCP tool from the orchestrator. The model never selects tools: only this code calls MCP (after
    /// <c>get_schema_context</c> and, when SQL is valid, <c>execute_query</c>).
    /// </summary>
    private static async Task<string> ExecuteToolCallAsync(
        McpClient mcp,
        HashSet<string> validToolNames,
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken)
    {
        if (!validToolNames.Contains(toolName))
        {
            return JsonSerializer.Serialize(new
            {
                error = "Unknown function.",
                detail =
                    $"No tool named '{toolName}'. Valid tools: {string.Join(", ", validToolNames.OrderBy(static n => n, StringComparer.Ordinal))}."
            });
        }

        if (string.Equals(toolName, "execute_query", StringComparison.Ordinal) &&
            !TryGetNonEmptySqlArgument(args, out _))
        {
            return JsonSerializer.Serialize(new
            {
                error = "Bad arguments.",
                detail = "Missing required parameter: sql (one non-empty read-only SELECT or WITH ... SELECT string)."
            });
        }

        var toolResult = await mcp.CallToolAsync(toolName, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return FlattenToolResult(toolResult);
    }

    private static void LogToolCompletion(ILogger log, int round, string toolName, IReadOnlyDictionary<string, object?> args,
        string resultText)
    {
        if (!log.IsEnabled(LogLevel.Information))
            return;
        if (TryGetNonEmptySqlArgument(args, out var sql))
            log.LogInformation("[trace] round={Round} tool={Tool} sql={Sql}", round, toolName, TruncateForLog(sql, 2000));
        else if (args.Count > 0)
        {
            var summary = TruncateForLog(JsonSerializer.Serialize(args), 1200);
            log.LogInformation("[trace] round={Round} tool={Tool} args={Args}", round, toolName, summary);
        }
        else
            log.LogInformation("[trace] round={Round} tool={Tool}", round, toolName);

        log.LogInformation("[trace] round={Round} tool={Tool} result={Result}", round, toolName,
            TruncateForLog(resultText.ReplaceLineEndings(" "), 2000));
    }

    private static string TruncateForLog(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        if (text.Length <= maxChars)
            return text;
        return string.Concat(text.AsSpan(0, maxChars), "… (+", (text.Length - maxChars).ToString(), " chars)");
    }

    private static bool TryGetNonEmptySqlArgument(IReadOnlyDictionary<string, object?> args, out string sql)
    {
        sql = "";
        if (!args.TryGetValue("sql", out var o) || o is null)
            return false;
        sql = o switch
        {
            string s => s.Trim(),
            _ => o.ToString()?.Trim() ?? ""
        };
        return sql.Length > 0;
    }

    /// <summary>Failure = guard error, MySQL error object, or non-JSON. Success = JSON array of rows (possibly empty).</summary>
    private static bool IsExecuteQueryFailureResult(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
            return true;
        try
        {
            using var doc = JsonDocument.Parse(resultText);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return false;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
                return true;
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static JsonNode? ArgToJsonNode(object? v) =>
        v switch
        {
            null => null,
            JsonElement je => JsonNode.Parse(je.GetRawText()),
            bool b => b,
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            string s => s,
            _ => JsonValue.Create(v.ToString())
        };

    private static string FlattenToolResult(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
            return result.IsError == true ? "Error (no details)." : "";

        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock txt)
                sb.Append(txt.Text);
            else
                sb.Append(JsonSerializer.Serialize(block));
        }

        return sb.Length > 0 ? sb.ToString() : JsonSerializer.Serialize(result);
    }

    private static string ExtractAssistantText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return "";
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(part.GetString());
                }
                else if (part.ValueKind == JsonValueKind.Object)
                {
                    if (part.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(te.GetString());
                    }
                }
            }
            if (sb.Length > 0)
                return sb.ToString();
        }
        return content.GetRawText();
    }

    private static string? PrettyPrintJsonIfPossible(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;
        try
        {
            using var d = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(d.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static string ResolveMcpProjectPath(IConfiguration config)
    {
        var configured = config["McpServer:ProjectFile"];
        var relativeDefault = "MySqlMcpServer/MySqlMcpServer.csproj";
        var rel = string.IsNullOrWhiteSpace(configured) ? relativeDefault : configured!;

        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(root);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, rel);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Could not find MCP project file '{rel}'. Set McpServer:ProjectFile to a full path, or run from the Db_Agent folder.");
    }
}
