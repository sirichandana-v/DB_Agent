## Feature: Response Sanitization for Ollama SQL Output

### Root cause (what we found)

**The problem was constraining the Ollama request with `format: "json"`** (Ollama API:
[`format` field](https://github.com/ollama/ollama/blob/main/docs/api.md#generate-a-chat-completion)).
That option forces the model to emit *valid* JSON, which competes with or degrades
generating a single raw SQL string for qwen2.5:7b in this flow.

**Required fix:** do **not** send `format: "json"` for **any** Ollama call in the
bridge. JSON mode is proven to break SQL generation. The bridge **never** adds
`format` to the request—no config toggle (avoids misconfiguration).

Prompt-only JSON instructions are secondary; the critical change is **disabling
API-level JSON mode**. Sanitization then recovers clean SQL from plain text (and
optionally from accidental `{"sql":"..."}` in the *body*, without relying on
schema-enforced output).

### Context

We call qwen2.5:7b via Ollama to generate MySQL SQL. After **removing** `format: "json"`,
the model returns **free-form text** (no API JSON constraint). We extract and sanitize
the SQL before executing it. Step 3 below still handles a JSON *wrapper* if the model
outputs it as characters in the string—it is not the same as Ollama’s `format: json`.

### What changed in the prompt

System prompt is now:
"You are a MySQL expert. Write only a valid MySQL SELECT query.
No explanation, no markdown, no JSON wrapper."

User message no longer has:
"Respond with one JSON object only. Shape: {"sql":"..."}..."

(Adjust prompt copy as needed; the non-negotiable part is **no** `format: "json"` on the
request.)

### Create: Services/SqlSanitizer.cs (in the project that owns the Ollama SQL path, e.g. OllamaMcpBridge)

Static class with a single public method:
`public static SanitizeResult Sanitize(string rawModelResponse)`

SanitizeResult is a record:
`public record SanitizeResult(bool Success, string Sql, string Error);`

### Sanitization pipeline (in order):

Step 1 - Trim whitespace
`rawModelResponse.Trim()`

Step 2 - Extract from markdown fences
If the model wrapped SQL in a Markdown code fence (optional `sql` after the
opening fence, or an untagged fence), extract the inner text only. Implement
with a regex that matches the opening fence line, then captures the body in a
group such as `([\s\S]*?)`, up to the closing fence line.

Step 3 - Extract from JSON if the model wrapped it anyway (plain text, not API `format: json`)
If trimmed result starts with `{` → attempt to parse as JSON
Look for `"sql"` property → extract its value
If JSON parse fails → use raw trimmed text as-is

Step 4 - Extract first statement only
Find first semicolon → take everything before it
If no semicolon → use full string
Trim again

Step 5 - Safety checks (return Success=false with clear Error if any fail):

- Empty check: must not be empty or whitespace
- Length check: must be under 2000 characters
- Must start with SELECT or WITH (case-insensitive trim)
- Forbidden keywords (case-insensitive, whole word match):
  INSERT, UPDATE, DELETE, DROP, ALTER, CREATE,
  TRUNCATE, EXEC, EXECUTE, XP\_, INTO, GRANT, REVOKE
- Multiple statements: must not contain `;` anywhere in the result
- PostgreSQL syntax: must not contain `::`
- Must not contain `/*` or `*/` (comment injection)
- Must not contain `--` followed by anything (comment injection)

Step 6 - Return `SanitizeResult(true, cleanSql, null)`

### Update: Ollama request builder (e.g. `OllamaMcpBridge/BridgeRunner.cs`)

**Do not** set `format` on the chat request body (no `format: "json"` ever;
**bridge-wide**). There is no `Ollama:JsonResponse` or similar setting.

**Replace** `TryParseModelSqlJson`-style JSON parsing of the
assistant message that assumed API JSON output, with:
`SqlSanitizer.Sanitize(rawContent)` on the model’s message content (same string you
would have passed to the JSON parser before).

If `SanitizeResult.Success` is false → return error to caller with `SanitizeResult.Error`
If Success → return the clean SQL string

**Optional:** rename log/trace labels from `direct-sql-json` to something like
`direct-sql` so it does not imply JSON mode is enabled.

### Update: `appsettings.json` (OllamaMcpBridge, BridgeWeb, or any host that sets this)

No `JsonResponse` key—removed in favor of hardcoded “never send `format: json`.”

Change system prompt to:
"You are a MySQL expert. Write only a valid MySQL SELECT query.
No explanation, no markdown, no JSON wrapper."

Remove from user message template:
"Respond with one JSON object only. Shape: {"sql":"..."}.
The sql value must be exactly one statement."

### Update: `ollama-chat-test.json` and similar curl fixtures

**Remove** `"format": "json"` from curl fixtures so local tests match production.

### Update: `DatabaseService.cs` `ExecuteQueryAsync`

No change needed — it already receives a plain SQL string and executes it.
The sanitization happens before this call.

### Logging

Log the raw model response before sanitization (for debugging)
Log the sanitized SQL after sanitization
Log which sanitization step was triggered (markdown extraction, JSON extraction etc)
Log any safety check failures with the reason

### Unit tests for SqlSanitizer (optional but recommended)

Test cases to cover:

- Clean SQL → passes through unchanged
- Fenced with language tag (sql) → extracted correctly
- Fenced with no language tag → extracted correctly
- JSON wrapped `{"sql":"..."}` → extracted correctly
- Starts with explanation text → fails (doesn't start with SELECT/WITH)
- Contains DELETE → fails forbidden keyword
- Contains `::` → fails PostgreSQL syntax
- Multiple semicolons → fails
- Empty response → fails
- Over 2000 chars → fails
- WITH ... SELECT (CTE) → passes

### Retry logic (e.g. in `BridgeRunner` where the direct-SQL Ollama call is made)

Max attempts: 2
On attempt 1 failure (sanitization fails or SQL execution fails):

- Log the failure and reason
- Do NOT feed error back to model
- Resend identical prompt fresh (no conversation history)
  On attempt 2 failure:
- Return clean error to user
- Log both attempts for debugging

### Decisions (locked in)

1. **Single vs per-path:** **Never send API JSON mode** for any Ollama call in the
   bridge (not special-cased per path).
2. **No config toggle:** **`format: json` is not optional**—removed from code and
   from `appsettings`; nothing to misconfigure.
3. **Ollama doc link:** **Keep** the GitHub Ollama API `format` link in the root
   cause section.
