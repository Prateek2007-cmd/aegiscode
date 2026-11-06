// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.SecurityScanning.Services;
using Xunit;

namespace MeisterDev.ProPR.SecurityScanning.Tests;

/// <summary>
///     Coverage for the grounding gate: an answer is grounded only when every citation it
///     carries names a snippet that was actually retrieved, and at least one is present.
///     NOTE: run on the laptop with <c>dotnet test</c>; testhost is broken in the VM.
/// </summary>
public sealed class CodeQaCitationGateTests
{
    [Fact]
    public void GroundedWhenAllCitationsAreInRange()
    {
        var check = CodeQaCitationGate.Validate("The handler lives in [1] and is called from [2].", 2);

        Assert.True(check.IsGrounded);
        Assert.Equal(new[] { 1, 2 }, check.CitedIndexes);
        Assert.Null(check.Problem);
    }

    [Fact]
    public void WithheldWhenACitationIsOutOfRange()
    {
        var check = CodeQaCitationGate.Validate("See the handler in [1] and the config in [9].", 2);

        Assert.False(check.IsGrounded);
        Assert.Contains("[9]", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithheldWhenCitationZeroIsUsed()
    {
        var check = CodeQaCitationGate.Validate("See [0] for details.", 2);

        Assert.False(check.IsGrounded);
        Assert.Contains("[0]", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithheldWhenTheAnswerCarriesNoCitations()
    {
        var check = CodeQaCitationGate.Validate("The handler validates the token before calling the store.", 2);

        Assert.False(check.IsGrounded);
        Assert.Empty(check.CitedIndexes);
        Assert.NotNull(check.Problem);
    }

    [Fact]
    public void WithheldWhenNothingWasRetrieved()
    {
        var check = CodeQaCitationGate.Validate("See [1].", 0);

        Assert.False(check.IsGrounded);
        Assert.Empty(check.CitedIndexes);
    }

    [Fact]
    public void NonNumericBracketsAreNotCitations()
    {
        var check = CodeQaCitationGate.Validate("The [handler] validates input, see [1].", 1);

        Assert.True(check.IsGrounded);
        Assert.Equal(new[] { 1 }, check.CitedIndexes);
    }

    [Fact]
    public void DuplicateCitationsCountOnce()
    {
        var check = CodeQaCitationGate.Validate("As shown in [1] and again in [1].", 1);

        Assert.True(check.IsGrounded);
        Assert.Equal(new[] { 1 }, check.CitedIndexes);
    }
}
