using System.Collections;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

        var ollamaBase = configuration["Ollama:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:11434";
        // Qwen 2.5 7B is widely recommended for local tool calling (see InsiderLLM / Ollama tool docs); llama3.2 often misfires.
        var model = configuration["Ollama:Model"] ?? "qwen2.5:7b";
        var maxIterations = configuration.GetValue("Ollama:MaxToolIterations", 24);
        var systemPrompt = configuration["Agent:SystemPrompt"] ?? "";
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

        if (!configuration.GetValue("Agent:UseLegacyToolLoop", false))
            return await RunDirectSqlJsonPipeline(userMessage, configuration, loggerFactory, mcp, cancellationToken)
                .ConfigureAwait(false);

        var mcpTools = await mcp.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var ollamaTools = BuildOllamaTools(mcpTools);
        var validToolNames = mcpTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = userMessage }
        };

        var toolCalls = new List<ToolCallRecord>();
        var brokenToolNudges = 0;
        const int maxBrokenToolNudges = 2;
        var dataFinishNudges = 0;
        const int maxDataFinishNudges = 3;

        using var http = new HttpClient { BaseAddress = new Uri(ollamaBase + "/") };
        for (var round = 0; round < maxIterations; round++)
        {
            var requestBody = new JsonObject
            {
                ["model"] = model,
                ["messages"] = JsonNode.Parse(messages.ToJsonString())!,
                ["tools"] = JsonNode.Parse(ollamaTools.ToJsonString())!,
                ["stream"] = false
            };
            ApplyOllamaChatOptions(configuration, requestBody);

            var requestJson = requestBody.ToJsonString();
            LogOllamaOutgoingRequest(configuration, log, "legacy-tool-loop", round, model, messages, requestBody, requestJson);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await http.PostAsync("api/chat", content, cancellationToken).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 2,
                    ErrorDetail = $"Ollama HTTP {(int)response.StatusCode}: {responseText}",
                    ToolCalls = toolCalls
                };
            }

            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message))
            {
                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 3,
                    ErrorDetail = "Ollama response missing message.\n" + responseText,
                    ToolCalls = toolCalls
                };
            }

            messages.Add(JsonNode.Parse(message.GetRawText())!);

            if (message.TryGetProperty("tool_calls", out var toolCallsEl) &&
                toolCallsEl.ValueKind == JsonValueKind.Array &&
                toolCallsEl.GetArrayLength() > 0)
            {
                foreach (var call in toolCallsEl.EnumerateArray())
                {
                    if (!call.TryGetProperty("function", out var fn))
                        continue;
                    var name = fn.GetProperty("name").GetString() ?? "";
                    var argMap = ParseFunctionArguments(fn);
                    var toolText = await ExecuteToolCallAsync(mcp, validToolNames, name, argMap, cancellationToken)
                        .ConfigureAwait(false);
                    toolCalls.Add(new ToolCallRecord { ToolName = name, Arguments = argMap, ResultText = toolText });
                    LogToolCompletion(log, round, name, argMap, toolText);
                    var toolMsg = new JsonObject { ["role"] = "tool", ["name"] = name, ["content"] = toolText };
                    if (call.TryGetProperty("id", out var idEl))
                        toolMsg["tool_call_id"] = idEl.GetString() ?? "";
                    messages.Add(toolMsg);
                }

                continue;
            }

            var finalText = ExtractAssistantText(message);
            if (TryParseContentAsToolCall(finalText, out var inferredName, out var inferredArgs))
            {
                var toolText = await ExecuteToolCallAsync(mcp, validToolNames, inferredName, inferredArgs, cancellationToken)
                    .ConfigureAwait(false);
                toolCalls.Add(new ToolCallRecord
                {
                    ToolName = inferredName,
                    Arguments = inferredArgs,
                    ResultText = toolText
                });
                LogToolCompletion(log, round, inferredName, inferredArgs, toolText);
                messages.Add(new JsonObject { ["role"] = "tool", ["name"] = inferredName, ["content"] = toolText });
                continue;
            }

            // Ollama sometimes emits broken JSON in message.content instead of native tool_calls (no "sql", truncated "parameters", etc.).
            if (LooksLikeBrokenInMessageToolCall(finalText))
            {
                if (brokenToolNudges < maxBrokenToolNudges)
                {
                    brokenToolNudges++;
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] =
                            "That reply was not a valid tool call. Do not print JSON tool objects in the message text. " +
                            "Invoke the registered functions only (native tool_calls / function calling). " +
                            "For execute_query the function arguments must include a non-empty \"sql\" string with a single SELECT. " +
                            "Continue: call get_schema_context if you still need it, then execute_query."
                    });
                    continue;
                }

                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 5,
                    ErrorDetail =
                        "The model returned malformed tool-like JSON in assistant text instead of using native tool_calls " +
                        "(common with some Ollama models). Try a model with stronger tool support (e.g. qwen2.5), upgrade Ollama, " +
                        "or ask again. Raw assistant text: " + finalText,
                    AssistantFinalText = finalText,
                    ToolCalls = toolCalls
                };
            }

            // After failed execute_query attempts, small models often emit a fake ASCII table instead of fixing SQL.
            if (AnyExecuteQuery(toolCalls) && !HadSuccessfulExecuteQuery(toolCalls))
            {
                if (dataFinishNudges < maxDataFinishNudges)
                {
                    dataFinishNudges++;
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] =
                            "You called execute_query but none succeeded (errors or invalid SQL). Do not invent rows, emails, or tables in prose. " +
                            "Call get_schema_context if needed, then execute_query again with a single valid read-only SELECT using exact column names from the schema " +
                            "(e.g. assignments.role_name, assignments.hours_allocated). When execute_query returns a JSON array, your final answer may only summarize values from that JSON."
                    });
                    continue;
                }

                return new BridgeRunResult
                {
                    Success = false,
                    ExitCode = 6,
                    ErrorDetail =
                        "The model answered in text without a successful execute_query after one or more failed queries; " +
                        "the reply may be hallucinated. Prefer Ollama model qwen2.5:7b or larger for tool use. Last assistant text: " +
                        finalText,
                    AssistantFinalText = finalText,
                    ToolCalls = toolCalls
                };
            }

            return new BridgeRunResult
            {
                Success = true,
                ExitCode = 0,
                AssistantFinalText = finalText,
                ToolCalls = toolCalls
            };
        }

        return new BridgeRunResult
        {
            Success = false,
            ExitCode = 4,
            ErrorDetail = $"Stopped after {maxIterations} tool rounds (safety limit).",
            ToolCalls = toolCalls
        };
    }

    private static async Task<BridgeRunResult> RunDirectSqlJsonPipeline(
        string userMessage,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        McpClient mcp,
        CancellationToken cancellationToken)
    {
        var log = loggerFactory.CreateLogger(nameof(BridgeRunner));
        var ollamaBase = configuration["Ollama:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:11434";
        var model = configuration["Ollama:Model"] ?? "qwen2.5:7b";
        var systemPrompt = configuration["Agent:SqlJsonSystemPrompt"] ?? DefaultSqlJsonSystemPrompt;

        var mcpTools = await mcp.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var validToolNames = mcpTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var toolCalls = new List<ToolCallRecord>();
        const int round = 0;

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
                ToolCalls = toolCalls
            };
        }

        if (string.IsNullOrWhiteSpace(schemaText))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 3,
                ErrorDetail = "get_schema_context returned empty schema.",
                ToolCalls = toolCalls
            };
        }

        var userContent = BuildSqlJsonUserPrompt(userMessage, schemaText, configuration);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = userContent }
        };

        var requestBody = new JsonObject
        {
            ["model"] = model,
            ["messages"] = JsonNode.Parse(messages.ToJsonString())!,
            ["stream"] = false
        };
        if (configuration.GetValue("Ollama:JsonResponse", true))
            requestBody["format"] = "json";
        ApplyOllamaChatOptions(configuration, requestBody);

        var requestJson = requestBody.ToJsonString();
        LogOllamaOutgoingRequest(configuration, log, "direct-sql-json", null, model, messages, requestBody, requestJson);

        using var http = new HttpClient { BaseAddress = new Uri(ollamaBase + "/") };
        using var chatContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
        chatContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await http.PostAsync("api/chat", chatContent, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 2,
                ErrorDetail = $"Ollama HTTP {(int)response.StatusCode}: {responseText}",
                ToolCalls = toolCalls
            };
        }

        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("message", out var message))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 3,
                ErrorDetail = "Ollama response missing message.\n" + responseText,
                ToolCalls = toolCalls
            };
        }

        var assistantText = ExtractAssistantText(message);
        if (!TryParseModelSqlJson(assistantText, out var sql, out var parseErr))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 8,
                ErrorDetail = parseErr + "\nRaw model output: " + assistantText,
                AssistantFinalText = assistantText,
                ToolCalls = toolCalls
            };
        }

        if (!SqlReadOnlyGuard.IsAllowedReadOnlySql(sql, out var guardErr))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 7,
                ErrorDetail = guardErr ?? "SQL failed read-only guard.",
                AssistantFinalText = assistantText,
                ToolCalls = toolCalls
            };
        }

        var execArgs = new Dictionary<string, object?> { ["sql"] = sql };
        var execResult = await ExecuteToolCallAsync(mcp, validToolNames, "execute_query", execArgs, cancellationToken)
            .ConfigureAwait(false);
        toolCalls.Add(new ToolCallRecord { ToolName = "execute_query", Arguments = execArgs, ResultText = execResult });
        LogToolCompletion(log, round, "execute_query", execArgs, execResult);

        if (IsExecuteQueryFailureResult(execResult))
        {
            return new BridgeRunResult
            {
                Success = false,
                ExitCode = 9,
                ErrorDetail = "execute_query failed or returned an error object.\n" + execResult,
                AssistantFinalText = assistantText,
                ToolCalls = toolCalls
            };
        }

        return new BridgeRunResult
        {
            Success = true,
            ExitCode = 0,
            AssistantFinalText = execResult,
            ToolCalls = toolCalls
        };
    }

    private const string DefaultSqlJsonSystemPrompt =
        "You are a MySQL query writer. Reply with a single JSON object only (no markdown fences, no commentary). " +
        "The object must have exactly one property \"sql\" whose value is one read-only MySQL statement: a single SELECT or WITH ... SELECT. " +
        "Use only identifiers that appear in the schema block in the user message.";

    private static string BuildSqlJsonUserPrompt(string userQuestion, string schemaText, IConfiguration configuration)
    {
        var hints = configuration["Agent:SqlJsonUserHints"];
        if (string.IsNullOrWhiteSpace(hints))
            hints = DefaultSqlJsonUserHints;

        var sb = new StringBuilder();
        sb.AppendLine("## Database schema (use only these identifiers verbatim)");
        sb.AppendLine(schemaText.Trim());
        sb.AppendLine();
        sb.AppendLine("## Query rules");
        sb.AppendLine(hints.Trim());
        sb.AppendLine();
        sb.AppendLine("## Question");
        sb.AppendLine(userQuestion.Trim());
        sb.AppendLine();
        sb.AppendLine("Respond with one JSON object only. Shape: {\"sql\":\"...\"}. The sql value must be exactly one statement.");
        return sb.ToString();
    }

    private const string DefaultSqlJsonUserHints =
        "Every identifier in sql must appear verbatim in the schema above (same spelling, including plural/singular). " +
        "If a name is not listed, it does not exist.\n" +
        "Typical joins: employees.department_id -> departments.id; employees link to projects through assignments " +
        "(assignments.employee_id -> employees.id, assignments.project_id -> projects.id). " +
        "Department site is departments.location (not site). Open projects often mean projects.end_date IS NULL. " +
        "Assignment role and hours are assignments.role_name and assignments.hours_allocated—not employees.role or employees.hours.\n" +
        "Common mistakes: tables named employee, department, project, or employee_project often do not exist here; use the schema text.\n" +
        "Read-only only: one SELECT or WITH ... SELECT. No INSERT, UPDATE, DELETE, DDL, or SELECT ... INTO.";

    private static bool TryParseModelSqlJson(string content, out string sql, out string? error)
    {
        sql = "";
        error = null;
        var normalized = NormalizeAssistantJsonContent(content);
        try
        {
            using var doc = JsonDocument.Parse(normalized);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sql", out var sqlEl) ||
                sqlEl.ValueKind != JsonValueKind.String)
            {
                error = "Expected a JSON object with a string property \"sql\".";
                return false;
            }

            sql = sqlEl.GetString()?.Trim() ?? "";
            if (sql.Length == 0)
            {
                error = "Property \"sql\" is empty.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "Model output is not valid JSON: " + ex.Message;
            return false;
        }
    }

    private static string NormalizeAssistantJsonContent(string content)
    {
        var s = content.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal))
            return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl >= 0)
            s = s[(firstNl + 1)..];
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        if (end > 0)
            s = s[..end];
        return s.Trim();
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

    /// <summary>Runs an MCP tool after validating name (hallucinated tools) and required arguments per InsiderLLM-style agent guards.</summary>
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

    private static bool AnyExecuteQuery(IReadOnlyList<ToolCallRecord> records) =>
        records.Any(r => string.Equals(r.ToolName, "execute_query", StringComparison.Ordinal));

    private static bool HadSuccessfulExecuteQuery(IReadOnlyList<ToolCallRecord> records)
    {
        foreach (var r in records)
        {
            if (!string.Equals(r.ToolName, "execute_query", StringComparison.Ordinal))
                continue;
            if (!IsExecuteQueryFailureResult(r.ResultText))
                return true;
        }

        return false;
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

    /// <summary>Qwen/Hermes-style wrappers: extract inner JSON from &lt;tool_call&gt;...&lt;/tool_call&gt;.</summary>
    private static string UnwrapToolCallTags(string text)
    {
        text = text.Trim();
        var m = Regex.Match(text, @"<tool_call>\s*([\s\S]*?)\s*</tool_call>", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : text;
    }

    /// <summary>Detects assistant text that looks like a truncated or invalid in-message tool JSON (not parseable, but names a known tool).</summary>
    private static bool LooksLikeBrokenInMessageToolCall(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = UnwrapToolCallTags(text);
        if (!t.StartsWith("{", StringComparison.Ordinal))
            return false;
        if (t.IndexOf("\"name\"", StringComparison.Ordinal) < 0)
            return false;
        var hasToolName = t.Contains("execute_query", StringComparison.Ordinal) ||
                          t.Contains("get_schema_context", StringComparison.Ordinal);
        if (!hasToolName)
            return false;
        // Parsed successfully would have been handled above; this is the failure case.
        return !TryParseContentAsToolCall(t, out _, out _);
    }

    private static bool TryParseContentAsToolCall(string? text, out string name, out Dictionary<string, object?> args)
    {
        name = "";
        args = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = UnwrapToolCallTags(text.Trim());
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (TryParseToolCallObject(doc.RootElement, out name, out args))
                return true;
        }
        catch (JsonException)
        {
            // Outer JSON invalid (common when "arguments" contains newlines / unescaped quotes).
        }

        if (TryParseHermesRawArgumentsString(text, out name, out args))
            return true;

        var nameMatchFallback = Regex.Match(text, @"""name""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        var sqlMatchFallback = Regex.Match(text, @"""sql""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
        if (nameMatchFallback.Success && sqlMatchFallback.Success)
        {
            name = nameMatchFallback.Groups[1].Value;
            args["sql"] = Regex.Unescape(sqlMatchFallback.Groups[1].Value);
            return name.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Qwen/Hermes often wraps calls in &lt;tool_call&gt; and puts raw SQL in <c>"arguments": "SELECT ..."</c>.
    /// That may be invalid JSON (newlines inside the string), so <see cref="JsonDocument.Parse"/> never runs
    /// <see cref="TryParseToolCallObject"/> string branch.
    /// </summary>
    private static bool TryParseHermesRawArgumentsString(string text, out string name, out Dictionary<string, object?> args)
    {
        name = "";
        args = new Dictionary<string, object?>();
        var nameMatch = Regex.Match(text, @"""name""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (!nameMatch.Success)
            return false;
        name = nameMatch.Groups[1].Value;

        var noArgTools = new[] { "get_schema_context", "list_tables", "refresh_schema" };
        if (noArgTools.Contains(name, StringComparer.Ordinal))
        {
            if (!TryExtractQuotedArgumentsPayload(text, out var payload) || string.IsNullOrWhiteSpace(payload))
            {
                args = new Dictionary<string, object?>();
                return true;
            }

            try
            {
                using var inner = JsonDocument.Parse(payload);
                args = JsonObjectToArgs(inner.RootElement);
                return true;
            }
            catch (JsonException)
            {
                args = new Dictionary<string, object?>();
                return true;
            }
        }

        if (string.Equals(name, "describe_table", StringComparison.Ordinal))
        {
            if (TryExtractQuotedArgumentsPayload(text, out var dtPayload) && !string.IsNullOrWhiteSpace(dtPayload))
            {
                try
                {
                    using var inner = JsonDocument.Parse(dtPayload);
                    args = JsonObjectToArgs(inner.RootElement);
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            return false;
        }

        if (!string.Equals(name, "execute_query", StringComparison.Ordinal))
            return false;
        if (!TryExtractQuotedArgumentsPayload(text, out var sql) || string.IsNullOrWhiteSpace(sql))
            return false;
        args = new Dictionary<string, object?> { ["sql"] = sql.Trim() };
        return true;
    }

    /// <summary>Reads <c>"arguments": "…"</c> or <c>"parameters": "…"</c> value using JSON string escape rules until closing quote before <c>}</c>.</summary>
    private static bool TryExtractQuotedArgumentsPayload(string text, out string payload)
    {
        payload = "";
        var keyIdx = text.IndexOf("\"arguments\"", StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0)
            keyIdx = text.IndexOf("\"parameters\"", StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0)
            return false;
        var i = text.IndexOf(':', keyIdx);
        if (i < 0)
            return false;
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i >= text.Length)
            return false;
        // Object form {"sql":"..."} — let JSON path handle; here we only scan quoted string form.
        if (text[i] != '"')
            return false;
        i++;
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var n = text[i + 1];
                switch (n)
                {
                    case '"':
                        sb.Append('"');
                        i += 2;
                        continue;
                    case '\\':
                        sb.Append('\\');
                        i += 2;
                        continue;
                    case 'n':
                        sb.Append('\n');
                        i += 2;
                        continue;
                    case 'r':
                        sb.Append('\r');
                        i += 2;
                        continue;
                    case 't':
                        sb.Append('\t');
                        i += 2;
                        continue;
                    default:
                        sb.Append(c);
                        i++;
                        continue;
                }
            }

            if (c == '"')
            {
                var j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                    j++;
                if (j >= text.Length || text[j] == '}')
                {
                    payload = sb.ToString();
                    return true;
                }
            }

            sb.Append(c);
            i++;
        }

        return false;
    }

    private static bool TryParseToolCallObject(JsonElement root, out string name, out Dictionary<string, object?> args)
    {
        name = "";
        args = new Dictionary<string, object?>();
        if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return false;
        name = nameEl.GetString() ?? "";
        if (name.Length == 0)
            return false;

        if (!root.TryGetProperty("arguments", out var paramsEl) && !root.TryGetProperty("parameters", out paramsEl))
            return false;

        if (paramsEl.ValueKind == JsonValueKind.String)
        {
            var s = paramsEl.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                if (string.Equals(name, "get_schema_context", StringComparison.Ordinal) ||
                    string.Equals(name, "list_tables", StringComparison.Ordinal) ||
                    string.Equals(name, "refresh_schema", StringComparison.Ordinal))
                {
                    args = new Dictionary<string, object?>();
                    return true;
                }

                return false;
            }

            try
            {
                using var inner = JsonDocument.Parse(s);
                args = JsonObjectToArgs(inner.RootElement);
                return true;
            }
            catch (JsonException)
            {
                // Qwen/Hermes often emit "arguments": "<raw SQL>" instead of {"sql":"..."} for execute_query.
                if (string.Equals(name, "execute_query", StringComparison.Ordinal))
                {
                    args = new Dictionary<string, object?> { ["sql"] = s.Trim() };
                    return true;
                }

                return false;
            }
        }

        if (paramsEl.ValueKind == JsonValueKind.Object)
        {
            args = JsonObjectToArgs(paramsEl);
            return true;
        }

        if (paramsEl.ValueKind == JsonValueKind.Array && paramsEl.GetArrayLength() > 0)
        {
            var first = paramsEl[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                args = JsonObjectToArgs(first);
                return true;
            }
        }

        return false;
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

    private static JsonArray BuildOllamaTools(IList<McpClientTool> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            JsonNode? parameters = JsonSerializer.SerializeToNode(t.ProtocolTool.InputSchema);
            if (parameters is null)
            {
                parameters = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                };
            }

            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description ?? "",
                    ["parameters"] = parameters
                }
            });
        }

        return arr;
    }

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
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        return "";
    }

    private static Dictionary<string, object?> ParseFunctionArguments(JsonElement function)
    {
        if (!function.TryGetProperty("arguments", out var argsEl))
            return new Dictionary<string, object?>();

        if (argsEl.ValueKind == JsonValueKind.String)
        {
            var s = argsEl.GetString();
            if (string.IsNullOrWhiteSpace(s))
                return new Dictionary<string, object?>();
            using var parsed = JsonDocument.Parse(s);
            return JsonObjectToArgs(parsed.RootElement);
        }

        return JsonObjectToArgs(argsEl);
    }

    private static Dictionary<string, object?> JsonObjectToArgs(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object)
            return d;
        foreach (var p in el.EnumerateObject())
            d[p.Name] = CoerceToolArgument(p.Value);
        return d;
    }

    private static object? CoerceToolArgument(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText()
        };

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
