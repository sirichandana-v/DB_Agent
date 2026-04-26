using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OllamaMcpBridge;

/// <summary>Extracts and validates a single read-only MySQL statement from free-form model output.</summary>
public static class SqlSanitizer
{
    public const int MaxSqlLength = 2000;

    public static SanitizeResult Sanitize(string rawModelResponse, ILogger? log = null)
    {
        var path = "trim";
        if (string.IsNullOrEmpty(rawModelResponse))
            return Report(log, "validation", "empty", SanitizeResult.Fail("Response is empty."));

        var t = rawModelResponse.Trim();
        log?.LogDebug("[sql-sanitizer] After trim, length={Len}", t.Length);

        if (TryExtractFromMarkdownFences(t, out var fromMd, out var mdPath))
        {
            t = fromMd;
            path = mdPath;
            log?.LogInformation("[sql-sanitizer] Extracted from markdown ({Path}). Length after extract={Len}", path, t.Length);
        }
        else
            log?.LogDebug("[sql-sanitizer] No markdown code fence; using text as after trim (path={Path})", path);

        t = t.Trim();
        if (t.StartsWith('{'))
        {
            if (TryParseSqlFromJsonObject(t, out var sqlFromJson, out var jsonErr))
            {
                t = sqlFromJson;
                path = "json-object-sql";
                log?.LogInformation("[sql-sanitizer] Extracted from JSON object (path={Path}).", path);
            }
            else
            {
                log?.LogDebug("[sql-sanitizer] JSON parse skipped or failed ({Err}); using text as-is.", jsonErr);
            }
        }

        t = t.Trim();
        var semi = t.IndexOf(';', StringComparison.Ordinal);
        if (semi >= 0)
        {
            if (t[(semi + 1)..].Trim().Length > 0)
                return Report(log, path, "multi-statement", SanitizeResult.Fail("Multiple statements are not allowed (content after first ';')."));
            t = t[..semi].Trim();
        }
        else
            t = t.Trim();
        log?.LogDebug("[sql-sanitizer] After first-statement cut, path={Path}, length={Len}", path, t.Length);

        if (t.Length == 0)
            return Report(log, path, "empty-after-pipeline", SanitizeResult.Fail("SQL is empty after extraction."));

        if (t.Length > MaxSqlLength)
            return Report(log, path, "length",
                SanitizeResult.Fail($"SQL exceeds max length {MaxSqlLength} (got {t.Length})."));

        if (t.Contains("::", StringComparison.Ordinal))
            return Report(log, path, "postgresql", SanitizeResult.Fail("SQL must not contain PostgreSQL-style '::' casts."));

        if (t.Contains("/*", StringComparison.Ordinal) || t.Contains("*/", StringComparison.Ordinal))
            return Report(log, path, "block-comment", SanitizeResult.Fail("SQL must not contain block comment markers /* */."));

        if (t.Contains("--", StringComparison.Ordinal))
            return Report(log, path, "line-comment", SanitizeResult.Fail("SQL must not contain line comments (--)."));

        if (t.Contains(';', StringComparison.Ordinal))
            return Report(log, path, "multi-statement", SanitizeResult.Fail("Multiple statements are not allowed (semicolon in SQL body)."));

        if (t.IndexOf("xp_", StringComparison.OrdinalIgnoreCase) >= 0)
            return Report(log, path, "xp", SanitizeResult.Fail("SQL must not reference extended stored procedures (xp_)."));

        if (ForbiddenKeywordRegex.IsMatch(t))
        {
            var m = ForbiddenKeywordRegex.Match(t);
            return Report(log, path, "forbidden-keyword",
                SanitizeResult.Fail($"Forbidden keyword or pattern near: \"{m.Value}\"."));
        }

        var start = t.TrimStart();
        if (start.Length == 0)
            return Report(log, path, "empty", SanitizeResult.Fail("SQL is empty."));

        if (!start.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !start.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return Report(log, path, "not-select", SanitizeResult.Fail("SQL must start with SELECT or WITH (after trim)."));
        }

        log?.LogInformation(
            "[sql-sanitizer] Sanitization ok (path={Path}). SQL length={Len}",
            path, t.Length);
        return new SanitizeResult(true, t, null);
    }

    private static SanitizeResult Report(ILogger? log, string path, string check, SanitizeResult result)
    {
        if (result is { Success: false, Error: { } e })
            log?.LogWarning("[sql-sanitizer] path={Path} check={Check} failed: {Error}", path, check, e);
        return result;
    }

    private static bool TryExtractFromMarkdownFences(string input, out string inner, out string path)
    {
        inner = input;
        path = "none";
        if (!input.Contains("```", StringComparison.Ordinal))
            return false;

        var m1 = SqlFenceStart.Match(input);
        if (m1.Success)
        {
            path = "markdown-fence-sql";
            inner = m1.Groups[1].Value.Trim();
            return true;
        }

        var m2 = GenericFence.Match(input);
        if (m2.Success)
        {
            path = "markdown-fence";
            inner = m2.Groups[1].Value.Trim();
            if (inner.StartsWith("sql", StringComparison.OrdinalIgnoreCase))
            {
                var lineBreak = inner.AsSpan(3).IndexOfAny("\r\n".AsSpan());
                if (lineBreak >= 0)
                    inner = inner[(3 + lineBreak)..].Trim();
            }
            return true;
        }

        var m3 = SqlFenceInline.Match(input);
        if (m3.Success)
        {
            path = "markdown-fence-sql-inline";
            inner = m3.Groups[1].Value.Trim();
            return true;
        }

        if (input.Contains("```", StringComparison.Ordinal))
        {
            var s = input.Trim();
            var open = s.IndexOf("```", StringComparison.Ordinal);
            var close = s.LastIndexOf("```", StringComparison.Ordinal);
            if (open >= 0 && close > open + 3)
            {
                var mid = s[(open + 3)..close];
                var n = mid.IndexOfAny("\r\n".ToCharArray());
                if (n >= 0)
                {
                    var first = mid[..n].Trim();
                    mid = first.Equals("sql", StringComparison.OrdinalIgnoreCase)
                        ? mid[(n + 1)..]
                        : mid;
                }
                path = "markdown-fence-fallback";
                inner = mid.Trim();
                return true;
            }
        }

        return false;
    }

    // ```sql (optional \r) \n  body  \n ```
    private static readonly Regex SqlFenceStart = new(
        @"(?s)```\s*sql\s*\r?\n([\s\S]*?)\r?\n\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ```\n body \n``` — first non-sql fence
    private static readonly Regex GenericFence = new(
        @"(?s)```[ \t]*\r?\n([\s\S]*?)\r?\n\s*```",
        RegexOptions.Compiled);

    private static readonly Regex SqlFenceInline = new(
        @"(?s)```\s*sql\s+([\s\S]*?)```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryParseSqlFromJsonObject(string text, out string sql, out string? error)
    {
        sql = "";
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "not an object";
                return false;
            }
            if (!doc.RootElement.TryGetProperty("sql", out var sqlEl) || sqlEl.ValueKind != JsonValueKind.String)
            {
                error = "no string sql";
                return false;
            }
            sql = sqlEl.GetString()?.Trim() ?? "";
            return sql.Length > 0;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Case-insensitive whole-word match for high-risk DML/DDL and dangerous patterns.</summary>
    private static readonly Regex ForbiddenKeywordRegex = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|INTO|GRANT|REVOKE)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

public sealed record SanitizeResult(bool Success, string Sql, string? Error)
{
    public static SanitizeResult Fail(string error) => new(false, "", error);
}
