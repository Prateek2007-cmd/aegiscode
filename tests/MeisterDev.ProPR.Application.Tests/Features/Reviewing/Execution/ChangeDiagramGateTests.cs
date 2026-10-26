// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Unit tests for the <see cref="ChangeDiagramGate" /> fail-closed verification of the
///     AI-generated Mermaid change diagram: a diagram is embedded only when it is non-empty, starts
///     with a supported Mermaid diagram keyword, and shows no obvious breakage. Every failure omits
///     the diagram silently — a broken diagram is never posted, and one is never faked.
/// </summary>
public sealed class ChangeDiagramGateTests
{
    [Fact]
    public void TryNormalize_Flowchart_PassesUnchanged()
    {
        const string diagram = "flowchart TD\n    A[Client] --> B[API]\n    B --> C[Database]";

        var passed = ChangeDiagramGate.TryNormalize(diagram, out var normalized, out var reason);

        Assert.True(passed);
        Assert.Equal(diagram, normalized);
        Assert.Equal(ChangeDiagramGateFailureReason.NoDiagram, reason);
    }

    [Fact]
    public void TryNormalize_GraphWithDirection_Passes()
    {
        Assert.True(ChangeDiagramGate.IsValid("graph LR\n    A --> B"));
    }

    [Theory]
    [InlineData("sequenceDiagram\n    Alice->>Bob: Hello")]
    [InlineData("classDiagram\n    class Foo")]
    [InlineData("stateDiagram-v2\n    [*] --> Idle")]
    [InlineData("erDiagram\n    CUSTOMER ||--o{ ORDER : places")]
    public void TryNormalize_SupportedDiagramTypes_Pass(string diagram)
    {
        Assert.True(ChangeDiagramGate.IsValid(diagram));
    }

    [Fact]
    public void TryNormalize_FencedMermaidBlock_StripsFences()
    {
        const string diagram = "```mermaid\nflowchart TD\n    A --> B\n```";

        var passed = ChangeDiagramGate.TryNormalize(diagram, out var normalized, out _);

        Assert.True(passed);
        Assert.Equal("flowchart TD\n    A --> B", normalized);
    }

    [Fact]
    public void TryNormalize_BareFencedBlock_StripsFences()
    {
        const string diagram = "```\ngraph TD\n    A --> B\n```";

        var passed = ChangeDiagramGate.TryNormalize(diagram, out var normalized, out _);

        Assert.True(passed);
        Assert.Equal("graph TD\n    A --> B", normalized);
    }

    [Fact]
    public void TryNormalize_NullOrWhitespace_FailsAsNoDiagram()
    {
        Assert.False(ChangeDiagramGate.TryNormalize(null, out var normalized, out var reason));
        Assert.Null(normalized);
        Assert.Equal(ChangeDiagramGateFailureReason.NoDiagram, reason);

        Assert.False(ChangeDiagramGate.TryNormalize("   \n ", out _, out reason));
        Assert.Equal(ChangeDiagramGateFailureReason.NoDiagram, reason);
    }

    [Fact]
    public void TryNormalize_ProseText_FailsAsUnsupportedType()
    {
        var passed = ChangeDiagramGate.TryNormalize(
            "This PR changes the login flow so users stay signed in.",
            out _,
            out var reason);

        Assert.False(passed);
        Assert.Equal(ChangeDiagramGateFailureReason.UnsupportedDiagramType, reason);
    }

    [Fact]
    public void TryNormalize_UnsupportedDiagramType_Fails()
    {
        var passed = ChangeDiagramGate.TryNormalize("pie\n    title Pets", out _, out var reason);

        Assert.False(passed);
        Assert.Equal(ChangeDiagramGateFailureReason.UnsupportedDiagramType, reason);
    }

    [Fact]
    public void TryNormalize_OpeningFenceWithoutClosing_FailsAsUnbalancedFences()
    {
        var passed = ChangeDiagramGate.TryNormalize("```mermaid\nflowchart TD\n    A --> B", out _, out var reason);

        Assert.False(passed);
        Assert.Equal(ChangeDiagramGateFailureReason.UnbalancedFences, reason);
    }

    [Fact]
    public void TryNormalize_FenceInsideBody_FailsAsUnbalancedFences()
    {
        var passed = ChangeDiagramGate.TryNormalize(
            "```mermaid\nflowchart TD\n    A --> B\n```\n```\n",
            out _,
            out var reason);

        Assert.False(passed);
        Assert.Equal(ChangeDiagramGateFailureReason.UnbalancedFences, reason);
    }

    [Fact]
    public void TryNormalize_UnbalancedBracket_Fails()
    {
        var passed = ChangeDiagramGate.TryNormalize("graph TD\n    A[Foo", out _, out var reason);

        Assert.False(passed);
        Assert.Equal(ChangeDiagramGateFailureReason.UnbalancedBrackets, reason);
    }

    [Fact]
    public void TryNormalize_ClosingBracketWithoutOpening_Fails()
    {
        var passed = ChangeDiagramGate.TryNormalize("graph TD\n    A] --> B", out _, out var reason);

        Assert.False(passed);
        Assert.Equal(ChangeDiagramGateFailureReason.UnbalancedBrackets, reason);
    }

    [Fact]
    public void TryNormalize_BracketsInsideQuotedLabel_DoNotFail()
    {
        // A bracket inside a node label is text, not structure.
        Assert.True(ChangeDiagramGate.IsValid("graph TD\n    A[\"x (y) [z]\"] --> B"));
    }

    [Fact]
    public void TryNormalize_OversizedDiagram_FailsAsTooLarge()
    {
        var diagram = "graph TD\n    A --> B\n" + new string('x', ChangeDiagramGate.MaxDiagramLength);

        var passed = ChangeDiagramGate.TryNormalize(diagram, out _, out var reason);

        Assert.False(passed);
        Assert.Equal(ChangeDiagramGateFailureReason.TooLarge, reason);
    }

    [Fact]
    public void TryNormalize_CrlfLineEndings_Normalized()
    {
        var passed = ChangeDiagramGate.TryNormalize("graph TD\r\n    A --> B\r\n", out var normalized, out _);

        Assert.True(passed);
        Assert.Equal("graph TD\n    A --> B", normalized);
    }
}
