// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Why the change-diagram gate dropped a diagram. Every reason is a fail-closed verdict: the
///     diagram is omitted from the published review, never repaired or replaced with a fabricated one.
/// </summary>
public enum ChangeDiagramGateFailureReason
{
    /// <summary>The AI produced no diagram at all; not a failure, only a pass-through.</summary>
    NoDiagram = 0,

    /// <summary>The diagram is empty or whitespace-only after trimming.</summary>
    Empty,

    /// <summary>The diagram is implausibly large for a change diagram.</summary>
    TooLarge,

    /// <summary>A code fence appears inside the diagram body, so it cannot be embedded safely.</summary>
    UnbalancedFences,

    /// <summary>The first line is not a supported Mermaid diagram type.</summary>
    UnsupportedDiagramType,

    /// <summary>A bracket, brace, or parenthesis opened in the diagram is never closed (or vice versa).</summary>
    UnbalancedBrackets,
}

/// <summary>
///     Fail-closed verification for the AI-generated Mermaid change diagram, mirroring
///     <see cref="SuggestedCodeGate" />: a diagram is embedded in the published review only when it is
///     non-empty, starts with a supported Mermaid diagram keyword, and shows no obvious breakage.
///     Anything else is omitted silently — a broken diagram is never posted, and one is never faked.
///     The gate is pure and synchronous, so it is unit-testable without a provider or the network.
///     Public so the provider-neutral summary renderer (a different assembly) can re-validate at
///     render time: a <c>ReviewResult</c> can be constructed anywhere, so the publisher must not
///     trust that the orchestration-time gate already ran.
/// </summary>
public static class ChangeDiagramGate
{
    /// <summary>Maximum accepted diagram size, in characters, after fence stripping and trimming.</summary>
    public const int MaxDiagramLength = 8000;

    /// <summary>Supported Mermaid diagram types, matched against the first token of the first line.</summary>
    public static readonly IReadOnlyList<string> SupportedDiagramTypes =
        ["graph", "flowchart", "sequenceDiagram", "classDiagram", "stateDiagram", "stateDiagram-v2", "erDiagram"];

    /// <summary>
    ///     Validates <paramref name="diagram" /> and, when it passes, returns the embeddable form:
    ///     fences stripped, line endings normalized, trimmed. When it fails,
    ///     <paramref name="normalized" /> is <see langword="null" /> and <paramref name="failureReason" />
    ///     says why. A <see langword="null" /> or whitespace diagram is not a failure of the content —
    ///     it reports <see cref="ChangeDiagramGateFailureReason.NoDiagram" /> — the caller simply omits
    ///     the section.
    /// </summary>
    public static bool TryNormalize(string? diagram, out string? normalized, out ChangeDiagramGateFailureReason failureReason)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(diagram))
        {
            failureReason = ChangeDiagramGateFailureReason.NoDiagram;
            return false;
        }

        var text = diagram.Trim();
        if (text.Contains("```", StringComparison.Ordinal))
        {
            // A fenced diagram is accepted only as exactly one leading fence and one trailing fence:
            // anything else means a fence inside the body, which would terminate the block the
            // publisher renders around it.
            if (!TryStripFences(text, out text))
            {
                failureReason = ChangeDiagramGateFailureReason.UnbalancedFences;
                return false;
            }
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (text.Length == 0)
        {
            failureReason = ChangeDiagramGateFailureReason.Empty;
            return false;
        }

        if (text.Length > MaxDiagramLength)
        {
            failureReason = ChangeDiagramGateFailureReason.TooLarge;
            return false;
        }

        var firstLine = text.Split('\n')[0].Trim();
        var firstToken = firstLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!SupportedDiagramTypes.Contains(firstToken, StringComparer.Ordinal))
        {
            failureReason = ChangeDiagramGateFailureReason.UnsupportedDiagramType;
            return false;
        }

        if (!HasBalancedBrackets(StripQuoted(text)))
        {
            failureReason = ChangeDiagramGateFailureReason.UnbalancedBrackets;
            return false;
        }

        normalized = text;
        failureReason = ChangeDiagramGateFailureReason.NoDiagram;
        return true;
    }

    /// <summary>Whether the diagram would pass the gate, without returning the normalized form.</summary>
    public static bool IsValid(string? diagram) =>
        TryNormalize(diagram, out _, out _);

    private static bool TryStripFences(string text, out string stripped)
    {
        stripped = text;
        var lines = text.Split('\n');
        if (lines.Length < 3)
        {
            return false;
        }

        var first = lines[0].Trim();
        if (!first.StartsWith("```", StringComparison.Ordinal))
        {
            return false;
        }

        // The opening fence may name the language (```mermaid) or be bare (```); anything else on
        // that line is not a fence the publisher can reproduce.
        var fenceTag = first[3..].Trim();
        if (fenceTag.Length != 0 && !string.Equals(fenceTag, "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(lines[^1].Trim(), "```", StringComparison.Ordinal))
        {
            return false;
        }

        var inner = string.Join("\n", lines[1..^1]);
        if (inner.Contains("```", StringComparison.Ordinal))
        {
            return false;
        }

        stripped = inner;
        return true;
    }

    /// <summary>
    ///     Removes double- and single-quoted spans before bracket counting, so a bracket inside a node
    ///     label (for example <c>A["x (y)"]</c>) cannot fail an otherwise well-formed diagram.
    /// </summary>
    private static string StripQuoted(string text)
    {
        var result = new char[text.Length];
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                result[i] = ' ';
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                result[i] = ' ';
                continue;
            }

            result[i] = c;
        }

        return new string(result);
    }

    private static bool HasBalancedBrackets(string text)
    {
        var parens = 0;
        var brackets = 0;
        var braces = 0;
        foreach (var c in text)
        {
            switch (c)
            {
                case '(': parens++; break;
                case ')':
                    if (--parens < 0)
                    {
                        return false;
                    }

                    break;
                case '[': brackets++; break;
                case ']':
                    if (--brackets < 0)
                    {
                        return false;
                    }

                    break;
                case '{': braces++; break;
                case '}':
                    if (--braces < 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return parens == 0 && brackets == 0 && braces == 0;
    }
}
