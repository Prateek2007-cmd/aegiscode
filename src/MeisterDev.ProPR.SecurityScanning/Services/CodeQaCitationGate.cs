// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.SecurityScanning.Services;

/// <summary>Outcome of the grounding check applied to a generated code answer.</summary>
internal sealed record CodeQaCitationCheck(
    bool IsGrounded,
    IReadOnlyList<int> CitedIndexes,
    string? Problem);

/// <summary>
///     Fail-closed grounding gate for code answers. An answer is grounded only when every
///     citation it carries (of the form <c>[n]</c>) names a snippet that was actually retrieved,
///     and at least one such citation is present. An answer that cites a source that was never
///     retrieved, or that carries no citations at all, is withheld — the model is never allowed
///     to present an uncited claim as knowledge about the codebase.
/// </summary>
internal static partial class CodeQaCitationGate
{
    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex CitationPattern();

    /// <summary>Checks a generated answer against the number of retrieved snippets.</summary>
    public static CodeQaCitationCheck Validate(string answer, int citationCount)
    {
        if (citationCount <= 0)
        {
            return new CodeQaCitationCheck(false, [], "No code was retrieved to ground the answer.");
        }

        var cited = CitationPattern()
            .Matches(answer)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        if (cited.Count == 0)
        {
            return new CodeQaCitationCheck(
                false,
                [],
                "The model answered without citing any retrieved code, so the answer is withheld.");
        }

        var outOfRange = cited.Where(index => index < 1 || index > citationCount).ToList();
        if (outOfRange.Count > 0)
        {
            return new CodeQaCitationCheck(
                false,
                cited,
                $"The model cited source [{outOfRange[0]}], which was not retrieved, so the answer is withheld.");
        }

        return new CodeQaCitationCheck(true, cited, null);
    }
}
