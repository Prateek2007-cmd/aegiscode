// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Identifies PostgreSQL data-modification statements in EF command batches.</summary>
/// <remarks>
/// Infrastructure uses <c>DiscussionDatabaseMetricsInterceptor</c> to count reads and writes, and
/// <c>DiscussionWriteFenceInterceptor</c> to validate observation ownership before evidence writes. For example, <c>UPDATE retained_threads SET status = @status</c> requires a fence,
/// while <c>SELECT * FROM retained_threads FOR NO KEY UPDATE</c> only locks rows. A data-modifying CTE such as
/// <c>WITH removed AS (DELETE FROM retained_threads RETURNING id) SELECT * FROM removed</c> is also a write.
/// </remarks>
public static class DiscussionSqlCommandClassifier
{
    public static bool IsWrite(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var depth = 0;
        var first = true;
        var with = false;
        var previous = "";
        var beforePrevious = "";
        var thirdPrevious = "";
        foreach (var token in Tokens(sql))
        {
            if (token == ";")
            {
                first = true;
                with = false;
                depth = 0;
                previous = beforePrevious = thirdPrevious = "";
                continue;
            }

            if (token == "(")
            {
                depth++;
                continue;
            }

            if (token == ")")
            {
                depth--;
                continue;
            }

            if (first)
            {
                first = false;
                with = token == "WITH";
                if (token is "INSERT" or "UPDATE" or "DELETE" or "MERGE")
                {
                    return true;
                }
            }
            else if (with)
            {
                var lockingUpdate = token == "UPDATE" &&
                                    (previous == "FOR" || thirdPrevious == "FOR" && beforePrevious == "NO" && previous == "KEY");
                if (token is "INSERT" or "DELETE" or "MERGE" || token == "UPDATE" && !lockingUpdate)
                {
                    return true;
                }

                if (depth == 0 && token == "SELECT")
                {
                    with = false;
                }
            }

            thirdPrevious = beforePrevious;
            beforePrevious = previous;
            previous = token;
        }

        return false;
    }

    private static IEnumerable<string> Tokens(string sql)
    {
        for (var index = 0; index < sql.Length;)
        {
            var current = sql[index];
            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }
            }
            else if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index = SkipBlockComment(sql, index);
            }
            else if (current is '\'' or '"')
            {
                index = SkipQuoted(sql, index, false);
            }
            else if (current is 'e' or 'E' && index + 1 < sql.Length && sql[index + 1] == '\'')
            {
                index = SkipQuoted(sql, index + 1, true);
            }
            else if (current == '$' && TrySkipDollarQuoted(sql, index, out var end))
            {
                index = end;
            }
            else if (char.IsLetterOrDigit(current) || current == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }

                yield return sql[start..index].ToUpperInvariant();
            }
            else
            {
                index++;
                if (current is '(' or ')' or ';')
                {
                    yield return current.ToString();
                }
            }
        }
    }

    private static int SkipBlockComment(string sql, int index)
    {
        var nesting = 1;
        index += 2;
        while (index + 1 < sql.Length)
        {
            if (sql[index] == '/' && sql[index + 1] == '*')
            {
                nesting++;
                index += 2;
            }
            else if (sql[index] == '*' && sql[index + 1] == '/')
            {
                index += 2;
                if (--nesting == 0)
                {
                    return index;
                }
            }
            else
            {
                index++;
            }
        }

        return sql.Length;
    }

    private static int SkipQuoted(string sql, int index, bool escapeBackslashes)
    {
        var quote = sql[index++];
        while (index < sql.Length)
        {
            var current = sql[index++];
            if (escapeBackslashes && current == '\\' && index < sql.Length)
            {
                index++;
            }
            else if (current == quote)
            {
                if (index >= sql.Length || sql[index] != quote)
                {
                    return index;
                }

                index++;
            }
        }

        return sql.Length;
    }

    private static bool TrySkipDollarQuoted(string sql, int start, out int end)
    {
        end = start;
        var index = start + 1;
        if (index < sql.Length && (char.IsLetter(sql[index]) || sql[index] == '_'))
        {
            while (++index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
            {
            }
        }

        if (index >= sql.Length || sql[index] != '$')
        {
            return false;
        }

        var delimiter = sql[start..(index + 1)];
        var close = sql.IndexOf(delimiter, index + 1, StringComparison.Ordinal);
        end = close < 0 ? sql.Length : close + delimiter.Length;
        return true;
    }
}
