// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class AiReviewOptionsJudgeBudgetTests
{
    [Fact]
    public void JudgeBudgets_DefaultTo4000And8000Tokens()
    {
        var options = new AiReviewOptions();

        Assert.Equal(4000, options.EvidenceJudgeMaxOutputTokens);
        Assert.Equal(8000, options.EvidenceJudgeRetryMaxOutputTokens);
        Assert.True(options.HasValidEvidenceJudgeOutputBudgets());
    }

    [Fact]
    public void Bind_ReadsBothJudgeBudgetsFromTheirEnvironmentKeys()
    {
        var options = new AiReviewOptions();

        AiReviewOptionsBinder.Bind(options, Configuration(6000, 12000));

        Assert.Equal(6000, options.EvidenceJudgeMaxOutputTokens);
        Assert.Equal(12000, options.EvidenceJudgeRetryMaxOutputTokens);
    }

    [Theory]
    [InlineData(0, 8000)]
    [InlineData(511, 8000)]
    [InlineData(4000, 32769)]
    [InlineData(4000, 4000)]
    [InlineData(8000, 4000)]
    public void HostOptions_RejectUnboundedOrNonIncreasingJudgeBudgets(int initial, int retry)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureSupport(Configuration(initial, retry));
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AiReviewOptions>>().Value);
    }

    [Fact]
    public void HostOptions_JudgeValidationDoesNotChangeOtherReviewTuningValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureSupport(Configuration(4000, 8000, "0"));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(0, provider.GetRequiredService<IOptions<AiReviewOptions>>().Value.MaxIterations);
    }

    private static IConfiguration Configuration(int initial, int retry, string iterations = "20")
    {
        return new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AI_EVIDENCE_JUDGE_MAX_OUTPUT_TOKENS"] = initial.ToString(),
                ["AI_EVIDENCE_JUDGE_RETRY_MAX_OUTPUT_TOKENS"] = retry.ToString(),
                ["AI_MAX_REVIEW_ITERATIONS"] = iterations,
                ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
                ["ADO_STUB_PR"] = "true",
                ["MEISTER_JWT_SECRET"] = "test-judge-budget-jwt-secret-32-characters!",
            }).Build();
    }
}
