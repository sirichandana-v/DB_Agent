namespace MySqlMcpServer;

/// <summary>Shared with OllamaMcpBridge for the direct SQL-JSON pipeline (pre-execute sanity check).</summary>
public static class SqlReadOnlyGuard
{
    public static bool IsAllowedReadOnlySql(string sql, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sql))
        {
            error = "SQL is empty.";
            return false;
        }

        var trimmed = StripLeadingCommentsAndWhitespace(sql.AsSpan());
        if (trimmed.Length == 0)
        {
            error = "SQL is empty after removing comments.";
            return false;
        }

        if (ContainsStatementSeparatorOutsideQuotes(trimmed))
        {
            error = "Multiple SQL statements are not allowed.";
            return false;
        }

        var upper = trimmed.ToString().ToUpperInvariant();
        if (!upper.StartsWith("SELECT", StringComparison.Ordinal) && !upper.StartsWith("WITH", StringComparison.Ordinal))
        {
            error = "Only SELECT or WITH (CTE) queries are allowed.";
            return false;
        }

        return true;
    }

    private static ReadOnlySpan<char> StripLeadingCommentsAndWhitespace(ReadOnlySpan<char> s)
    {
        while (s.Length > 0)
        {
            s = s.TrimStart();
            if (s.Length == 0)
                break;

            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                var nl = s.IndexOfAny("\r\n".AsSpan());
                s = nl < 0 ? ReadOnlySpan<char>.Empty : s[(nl + 1)..];
                continue;
            }

            if (s.Length > 0 && s[0] == '#')
            {
                var nl = s.IndexOfAny("\r\n".AsSpan());
                s = nl < 0 ? ReadOnlySpan<char>.Empty : s[(nl + 1)..];
                continue;
            }

            if (s.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = s.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                    return ReadOnlySpan<char>.Empty;
                s = s[(end + 2)..];
                continue;
            }

            break;
        }

        return s.TrimStart();
    }

    private static bool ContainsStatementSeparatorOutsideQuotes(ReadOnlySpan<char> sql)
    {
        bool inSingle = false, inDouble = false, inBacktick = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'' && !inDouble && !inBacktick)
            {
                inSingle = !inSingle;
                continue;
            }

            if (c == '"' && !inSingle && !inBacktick)
            {
                inDouble = !inDouble;
                continue;
            }

            if (c == '`' && !inSingle && !inDouble)
            {
                inBacktick = !inBacktick;
                continue;
            }

            if (c == ';' && !inSingle && !inDouble && !inBacktick)
            {
                var rest = sql[(i + 1)..].Trim();
                if (rest.Length > 0)
                    return true;
            }
        }

        return false;
    }
}
