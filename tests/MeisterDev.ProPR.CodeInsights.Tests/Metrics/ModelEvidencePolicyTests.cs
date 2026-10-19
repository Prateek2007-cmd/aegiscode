// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ModelEvidencePolicyTests
{
    [Fact]
    public void EnvironmentPolicyBindsCooldownIndependentlyOfTheExistingLease()
    {
        var options = new CodeInsightsOptions();
        CodeInsightsModuleServiceCollectionExtensions.BindOptions(
            options, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                    { ["CODE_INSIGHTS_MODEL_EVIDENCE_RETRY_COOLDOWN_MINUTES"] = "17", ["CODE_INSIGHTS_PROVIDER_EVIDENCE_LEASE_MINUTES"] = "3" }).Build());
        Assert.Equal(TimeSpan.FromMinutes(17), options.ModelEvidenceRetryCooldown);
        Assert.Equal(TimeSpan.FromMinutes(3), options.ProviderEvidenceLease);
        options.ModelEvidenceRetryCooldownMinutes = 0;
        Assert.Equal(TimeSpan.FromMinutes(1), options.ModelEvidenceRetryCooldown);
    }
}
