// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using System.Text;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Validates bounded discovery pages without following provider-supplied URLs.</summary>
internal static class ReviewDiscoveryPaging
{
    internal static int PageNumber(string? continuation)
    {
        if (continuation is null)
        {
            return 1;
        }

        var parts = continuation.Split(':');
        if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var page) ||
            page is < 2 or > 10001 || page.ToString(CultureInfo.InvariantCulture) != parts[0] ||
            parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var observed) ||
                                  observed is < 1 or > 10000 || observed.ToString(CultureInfo.InvariantCulture) != parts[1]))
        {
            throw new InvalidOperationException("The provider returned an invalid discovery continuation.");
        }

        return page;
    }

    internal static async Task<IReadOnlyList<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var root = await ReviewOverviewReadSession.ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (root is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() > 100 ||
            array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidOperationException("The provider returned an invalid discovery page.");
        }

        return array.Deserialize<IReadOnlyList<T>>() ?? throw new InvalidOperationException("The provider returned an invalid discovery page.");
    }

    internal static int? Total(HttpResponseMessage response, string header)
    {
        if (!response.Headers.TryGetValues(header, out var values))
        {
            return null;
        }

        var entries = values.ToArray();
        if (entries.Length != 1 || !int.TryParse(entries[0], NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total < 0)
        {
            throw InvalidContinuation();
        }

        return total;
    }

    internal static string? Next(
        HttpResponseMessage response, Uri requestedUri, int page, int rawCount,
        bool numberedHeader = false, int? total = null, string? continuation = null)
    {
        int? next = null;
        bool? hasMore = null;
        if (numberedHeader && response.Headers.TryGetValues("X-Next-Page", out var values))
        {
            var entries = values.ToArray();
            if (entries.Length != 1)
            {
                throw InvalidContinuation();
            }

            if (string.IsNullOrWhiteSpace(entries[0]))
            {
                hasMore = false;
            }
            else
            {
                next = ParseNext(entries[0], page);
                hasMore = true;
            }
        }
        else if (response.Headers.TryGetValues("Link", out var links))
        {
            hasMore = false;
            foreach (var link in links.SelectMany(ReadLinks))
            {
                if (link.Relation is null ||
                    !link.Relation.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("next", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (hasMore == true ||
                    !Uri.TryCreate(requestedUri, link.Target, out var uri) ||
                    uri.Scheme != requestedUri.Scheme || uri.Host != requestedUri.Host || uri.Port != requestedUri.Port ||
                    uri.AbsolutePath != requestedUri.AbsolutePath || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                {
                    throw InvalidContinuation();
                }

                var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Split('=', 2)).Where(value => Uri.UnescapeDataString(value[0]) == "page").ToArray();
                if (parameters.Length != 1 || parameters[0].Length != 2)
                {
                    throw InvalidContinuation();
                }

                next = ParseNext(Uri.UnescapeDataString(parameters[0][1]), page);
                hasMore = true;
            }
        }

        var prior = continuation?.Split(':') is { Length: 2 } parts ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var observed = prior + rawCount;
        if (rawCount == 0 && hasMore == true || total.HasValue && observed < total && (rawCount == 0 || hasMore == false))
        {
            throw InvalidContinuation();
        }

        var more = hasMore ?? (total.HasValue ? observed < total : rawCount == 100);
        if (!more)
        {
            return null;
        }

        var nextPage = (next ?? page + 1).ToString(CultureInfo.InvariantCulture);
        return nextPage + ":" + observed.ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<(string Target, string? Relation)> ReadLinks(string header)
    {
        var result = new List<(string, string?)>();
        var position = 0;
        do
        {
            SkipWhitespace(header, ref position);
            if (position >= header.Length || header[position++] != '<')
            {
                throw InvalidContinuation();
            }

            var start = position;
            while (position < header.Length && header[position] != '>')
            {
                if (header[position] <= ' ' || header[position] is '<' or '"' || header[position] == '\u007f')
                {
                    throw InvalidContinuation();
                }

                position++;
            }

            if (position == header.Length)
            {
                throw InvalidContinuation();
            }

            var target = header[start..position++];
            string? relation = null;
            SkipWhitespace(header, ref position);
            while (position < header.Length && header[position] == ';')
            {
                position++;
                SkipWhitespace(header, ref position);
                var name = ReadToken(header, ref position);
                SkipWhitespace(header, ref position);
                string? value = null;
                if (position < header.Length && header[position] == '=')
                {
                    position++;
                    SkipWhitespace(header, ref position);
                    value = position < header.Length && header[position] == '"'
                        ? ReadQuotedValue(header, ref position)
                        : ReadToken(header, ref position);
                    SkipWhitespace(header, ref position);
                }

                if (relation is null && name.Equals("rel", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw InvalidContinuation();
                    }

                    // Later occurrences of a relation parameter do not replace its first value.
                    relation = value;
                }
            }

            if (relation is null)
            {
                throw InvalidContinuation();
            }

            result.Add((target, relation));
            if (position == header.Length)
            {
                return result;
            }

            if (header[position++] != ',')
            {
                throw InvalidContinuation();
            }
        } while (true);
    }

    private static string ReadToken(string header, ref int position)
    {
        var start = position;
        while (position < header.Length && (char.IsAsciiLetterOrDigit(header[position]) ||
                                            "!#$%&'*+-.^_`|~".Contains(header[position], StringComparison.Ordinal)))
        {
            position++;
        }

        if (start == position)
        {
            throw InvalidContinuation();
        }

        return header[start..position];
    }

    private static string ReadQuotedValue(string header, ref int position)
    {
        position++;
        var value = new StringBuilder();
        while (position < header.Length)
        {
            var character = header[position++];
            if (character == '"')
            {
                return value.ToString();
            }

            if (character == '\\')
            {
                if (position == header.Length)
                {
                    throw InvalidContinuation();
                }

                character = header[position++];
            }

            if (character < ' ' && character != '\t' || character == '\u007f')
            {
                throw InvalidContinuation();
            }

            value.Append(character);
        }

        throw InvalidContinuation();
    }

    private static void SkipWhitespace(string header, ref int position)
    {
        while (position < header.Length && header[position] is ' ' or '\t')
        {
            position++;
        }
    }

    private static int ParseNext(string value, int page)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var next) ||
            next != page + 1 || next.ToString(CultureInfo.InvariantCulture) != value)
        {
            throw InvalidContinuation();
        }

        return next;
    }

    private static InvalidOperationException InvalidContinuation() => new("The provider returned an invalid discovery continuation.");
}
