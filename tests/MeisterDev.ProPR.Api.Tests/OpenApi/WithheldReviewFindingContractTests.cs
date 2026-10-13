// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace MeisterDev.ProPR.Api.Tests.OpenApi;

public sealed class WithheldReviewFindingContractTests
{
    [Fact]
    public void EmittedSchema_RequiresCommentAndBoundedNonblankReason()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Testing").UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true"));
        using var scope = factory.Services.CreateScope();
        var document = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var finding = document.Components!.Schemas!["WithheldReviewFinding"];

        Assert.Contains("comment", finding.Required!);
        Assert.Contains("reasonCode", finding.Required!);
        var reason = finding.Properties!["reasonCode"];
        Assert.Equal(JsonSchemaType.String, reason.Type);
        Assert.Equal(1, reason.MinLength);
        Assert.Equal(128, reason.MaxLength);
        Assert.NotNull(reason.Pattern);
        Assert.Matches(reason.Pattern, "judge_output_limit");
        Assert.DoesNotMatch(reason.Pattern, "   ");
    }
}
