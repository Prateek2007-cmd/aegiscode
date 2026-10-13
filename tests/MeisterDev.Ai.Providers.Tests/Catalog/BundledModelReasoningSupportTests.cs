// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Catalog;

namespace MeisterDev.Ai.Providers.Tests.Catalog;

public sealed class BundledModelReasoningSupportTests
{
    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5-mini", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("gpt-5-pro", false)]
    [InlineData("gateway-alias", false)]
    [InlineData("azure-gpt-5", false)]
    [InlineData("claude-sonnet-4-5", false)]
    public void LowEffort_RequiresAnExactModelThatAdvertisesTheValue(string modelId, bool expected)
    {
        Assert.Equal(expected, BundledModelReasoningSupport.SupportsLowEffort(modelId));
    }
}
