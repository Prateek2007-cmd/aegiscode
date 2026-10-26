// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for the Stage C synthesis parsing in <see cref="PrWideArtifactParser" />: the
///     optional PR-overview keys (PR summary, file walkthrough, change diagram) ride on the parsed
///     synthesis result when present, and stay null when absent — never invented.
/// </summary>
public sealed class PrWideArtifactParserTests
{
    [Fact]
    public void TryParseSynthesisResult_PrOverviewKeys_ParseOntoSynthesisResult()
    {
        const string json = """
            {
              "candidate_findings": [
                {
                  "id": "candidate-001",
                  "message": "Cache lookup misses the null check.",
                  "category": "per_file_comment",
                  "severity": "warning",
                  "confidence": { "concern": "correctness", "score": 80 },
                  "supporting_files": ["src/Cache.cs"],
                  "file_path": "src/Cache.cs",
                  "line_number": 12
                }
              ],
              "summary": "Draft summary.",
              "pr_summary": "Adds a login cache so repeat visits skip the database.",
              "walkthrough": [
                { "file_path": "src/Cache.cs", "description": "Adds the cache itself." },
                { "file_path": "src/Login.cs", "description": "Reads from the cache first." }
              ],
              "change_diagram": "flowchart TD\n    A[Login] --> B[Cache]"
            }
            """;

        var parsed = PrWideArtifactParser.TryParseSynthesisResult(json, out var synthesis);

        Assert.True(parsed);
        Assert.NotNull(synthesis);
        Assert.Equal("Adds a login cache so repeat visits skip the database.", synthesis!.PrSummary);
        Assert.NotNull(synthesis.Walkthrough);
        Assert.Equal(2, synthesis.Walkthrough!.Count);
        Assert.Equal("src/Cache.cs", synthesis.Walkthrough[0].FilePath);
        Assert.Equal("Adds the cache itself.", synthesis.Walkthrough[0].Description);
        Assert.Equal("flowchart TD\n    A[Login] --> B[Cache]", synthesis.ChangeDiagram);
        Assert.Single(synthesis.CandidateFindings);
    }

    [Fact]
    public void TryParseSynthesisResult_PrOverviewKeysAbsent_ParseAsNull()
    {
        const string json = """
            {
              "candidate_findings": [],
              "summary": "Draft summary."
            }
            """;

        var parsed = PrWideArtifactParser.TryParseSynthesisResult(json, out var synthesis);

        Assert.True(parsed);
        Assert.NotNull(synthesis);
        Assert.Null(synthesis!.PrSummary);
        Assert.Null(synthesis.Walkthrough);
        Assert.Null(synthesis.ChangeDiagram);
    }

    [Fact]
    public void TryParseSynthesisResult_WalkthroughEntriesMissingFields_AreSkipped()
    {
        const string json = """
            {
              "candidate_findings": [],
              "summary": "Draft summary.",
              "walkthrough": [
                { "file_path": "src/Cache.cs", "description": "Adds the cache itself." },
                { "file_path": "src/Login.cs" },
                { "description": "Has no path." },
                { "file_path": "  ", "description": "Blank path." }
              ]
            }
            """;

        var parsed = PrWideArtifactParser.TryParseSynthesisResult(json, out var synthesis);

        Assert.True(parsed);
        Assert.NotNull(synthesis!.Walkthrough);
        var entry = Assert.Single(synthesis.Walkthrough!);
        Assert.Equal("src/Cache.cs", entry.FilePath);
    }

    [Fact]
    public void TryParseSynthesisResult_WalkthroughNotAnArray_YieldsNullWalkthrough()
    {
        const string json = """
            {
              "candidate_findings": [],
              "summary": "Draft summary.",
              "walkthrough": "not an array"
            }
            """;

        var parsed = PrWideArtifactParser.TryParseSynthesisResult(json, out var synthesis);

        Assert.True(parsed);
        Assert.Null(synthesis!.Walkthrough);
    }
}
