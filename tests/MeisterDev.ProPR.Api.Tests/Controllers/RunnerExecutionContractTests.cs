// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

public sealed class RunnerExecutionContractTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string CommentJson = """{"filePath":"src/Foo.cs","lineNumber":3,"severity":"warning","message":"Potential issue."}""";

    private const string ValidEvidence =
        """[{"comment":{"filePath":"src/Foo.cs","lineNumber":3,"severity":"warning","message":"Potential issue."},"reasonCode":"judge_output_limit"}]""";

    [Theory]
    [InlineData("[{\"attempt\":1,\"files\":[\"src/Foo.cs\"]}]", false)]
    [InlineData("42", true)]
    public async Task IngestRelaysOptionalModelManifestsWithoutRejectingOrdinaryOutcomes(string manifests, bool malformed)
    {
        using var harness = new Harness();
        var body =
            CallJson().TrimEnd(
                '}') + ",\"sequence\":1,\"idempotencyKey\":\"proof\",\"fileResults\":[{\"filePath\":\"src/Foo.cs\",\"isComplete\":true,\"reviewedPassKeys\":[],\"comments\":["
                     + CommentJson + "]}],\"modelManifests\":" + manifests + "}";
        using var response = await harness.Client.PostAsync("/runners/execution/ingest", JsonRequest(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await harness.Ingest.Received(1).IngestAsync(
            Arg.Any<RunnerCallContext>(),
            Arg.Is<RunnerIngestBatch>(batch => batch.FileResults[0].Comments!.Count == 1 && batch.ModelManifests != null && batch.ModelManifests.Count == 1
                                               && (malformed
                                                   ? batch.ModelManifests[0].UnavailableReason == "model-proof-limit-exceeded"
                                                   : batch.ModelManifests[0].Files.Count == 1)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorResults_ResponseDeserializesAsItsAdvertisedTypeAndPreservesTheEnvelope(bool unavailable)
    {
        using var harness = new Harness();
        var withheld = new WithheldReviewFinding(new ReviewComment("src/Foo.cs", 3, CommentSeverity.Warning, "Potential issue."), "judge_output_limit");
        var prior = new RunnerPriorFileResult("src/Foo.cs", true, false, false, null, null, "Summary", [], [], WithheldFindings: [withheld]);
        harness.PriorResults.ReadAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                unavailable
                    ? RunnerToolResult<IReadOnlyList<RunnerPriorFileResult>>.NotOffered()
                    : RunnerToolResult<IReadOnlyList<RunnerPriorFileResult>>.Served([prior]));

        using var response = await harness.Client.PostAsync("/runners/execution/prior-results", JsonRequest(CallJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(unavailable, document.RootElement.GetProperty("unavailable").GetBoolean());
        if (unavailable)
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("value").ValueKind);
        }
        else
        {
            var checkpoint = Assert.Single(document.RootElement.GetProperty("value").EnumerateArray());
            Assert.Empty(checkpoint.GetProperty("comments").EnumerateArray());
            Assert.Equal(
                "judge_output_limit", Assert.Single(checkpoint.GetProperty("withheldFindings").EnumerateArray()).GetProperty("reasonCode").GetString());
        }

        var action = harness.Server.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items).Single(description => description.RelativePath == "runners/execution/prior-results");
        var responseType = Assert.Single(action.SupportedResponseTypes, metadata => metadata.StatusCode == 200).Type!;
        var restored = JsonSerializer.Deserialize(json, responseType, harness.JsonOptions);
        using var restoredJson = JsonDocument.Parse(JsonSerializer.Serialize(restored, responseType, harness.JsonOptions));
        Assert.Equal(document.RootElement.GetProperty("unavailable").GetRawText(), restoredJson.RootElement.GetProperty("unavailable").GetRawText());
        Assert.Equal(document.RootElement.GetProperty("value").GetRawText(), restoredJson.RootElement.GetProperty("value").GetRawText());
    }

    [Theory]
    [MemberData(nameof(MalformedEvidenceRequests))]
    public async Task MalformedWithheldEvidence_IsRejectedBeforeIngestOrSubmission(string route, string evidence)
    {
        using var harness = new Harness();

        using var response = await harness.Client.PostAsync($"/runners/execution/{route}", JsonRequest(RequestJson(route, evidence)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await harness.Ingest.DidNotReceiveWithAnyArgs().IngestAsync(default!, default!, default);
        await harness.Findings.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("ingest", null)]
    [InlineData("ingest", "null")]
    [InlineData("ingest", "[]")]
    [InlineData("ingest", ValidEvidence)]
    [InlineData("findings", null)]
    [InlineData("findings", "null")]
    [InlineData("findings", "[]")]
    [InlineData("findings", ValidEvidence)]
    public async Task ValidAndLegacyEvidence_PreservesPayloadAndReplayIdentifiers(string route, string? evidence)
    {
        using var harness = new Harness();
        var expectedCount = evidence == ValidEvidence ? 1 : 0;

        using var first = await harness.Client.PostAsync($"/runners/execution/{route}", JsonRequest(RequestJson(route, evidence)));
        using var replay = await harness.Client.PostAsync($"/runners/execution/{route}", JsonRequest(RequestJson(route, evidence)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        if (route == "ingest")
        {
            await harness.Ingest.Received(2).IngestAsync(
                Arg.Is<RunnerCallContext>(call => call.JobId == JobId),
                Arg.Is<RunnerIngestBatch>(batch => batch.Sequence == 1 && batch.IdempotencyKey == "same-batch"
                                                                       && (batch.FileResults[0].WithheldFindings == null
                                                                           ? 0
                                                                           : batch.FileResults[0].WithheldFindings!.Count) == expectedCount),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await harness.Findings.Received(2).SubmitAsync(
                Arg.Is<RunnerCallContext>(call => call.JobId == JobId),
                Arg.Is<RunnerFindingsChunk>(chunk => chunk.SubmissionId == "same-submission" && chunk.ChunkIndex == 0 && chunk.ChunkCount == 1
                                                     && (chunk.Annotations!.WithheldFindings == null ? 0 : chunk.Annotations.WithheldFindings.Count) ==
                                                     expectedCount),
                Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData("ingest", 1)]
    [InlineData("ingest", 128)]
    [InlineData("findings", 1)]
    [InlineData("findings", 128)]
    public async Task ReasonLengthBoundaries_AreAcceptedWithoutChangingEvidence(string route, int length)
    {
        using var harness = new Harness();
        var reason = new string('x', length);
        var evidence = $$"""[{"comment":{{CommentJson}},"reasonCode":"{{reason}}"}]""";

        using var response = await harness.Client.PostAsync($"/runners/execution/{route}", JsonRequest(RequestJson(route, evidence)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (route == "ingest")
        {
            await harness.Ingest.Received(1).IngestAsync(
                Arg.Any<RunnerCallContext>(),
                Arg.Is<RunnerIngestBatch>(batch => batch.FileResults[0].WithheldFindings![0].ReasonCode == reason),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await harness.Findings.Received(1).SubmitAsync(
                Arg.Any<RunnerCallContext>(),
                Arg.Is<RunnerFindingsChunk>(chunk => chunk.Annotations!.WithheldFindings![0].ReasonCode == reason),
                Arg.Any<CancellationToken>());
        }
    }

    public static IEnumerable<object[]> MalformedEvidenceRequests()
    {
        var findings = new[]
        {
            "null",
            """{"reasonCode":"judge_output_limit"}""",
            """{"comment":null,"reasonCode":"judge_output_limit"}""",
            $$"""{"comment":{{CommentJson}}}""",
            $$"""{"comment":{{CommentJson}},"reasonCode":null}""",
            $$"""{"comment":{{CommentJson}},"reasonCode":""}""",
            $$"""{"comment":{{CommentJson}},"reasonCode":"   "}""",
            $$"""{"comment":{{CommentJson}},"reasonCode":"{{new string('x', 129)}}"}""",
        };
        foreach (var route in new[] { "ingest", "findings" })
        {
            foreach (var finding in findings)
            {
                yield return [route, $"[{finding}]"];
            }
        }
    }

    private static StringContent JsonRequest(string json) => new(json, Encoding.UTF8, "application/json");

    private static string CallJson() => $$"""{"jobId":"{{JobId}}","leaseGeneration":1,"contractVersion":{{RunnerContractVersion.Current}}}""";

    private static string RequestJson(string route, string? evidence)
    {
        var withheldField = evidence is null ? string.Empty : $",\"withheldFindings\":{evidence}";
        return route == "ingest"
            ? $$$"""{"jobId":"{{{JobId}}}","leaseGeneration":1,"contractVersion":{{{RunnerContractVersion.Current}}},"sequence":1,"idempotencyKey":"same-batch","fileResults":[{"filePath":"src/Foo.cs","isComplete":true,"isFailed":false,"summary":"Summary","reviewedPassKeys":[],"comments":[]{{{withheldField}}}}]}"""
            : $$$"""{"jobId":"{{{JobId}}}","leaseGeneration":1,"contractVersion":{{{RunnerContractVersion.Current}}},"submissionId":"same-submission","chunkIndex":0,"chunkCount":1,"summary":"Summary","comments":[],"annotations":{"carriedForwardFilePaths":[],"carriedForwardCandidatesSkipped":0,"contextDegradedFilePaths":[],"contextSkippedFilePaths":[],"budgetSoftCapped":false,"budgetSoftCapSkippedFilePaths":[]{{{withheldField}}}}}""";
    }

    private sealed class Harness : IDisposable
    {
        private readonly IHost _host;

        public Harness()
        {
            Program.ConfigureJsonSerialization(this.JsonOptions);
            this.Ingest.IngestAsync(Arg.Any<RunnerCallContext>(), Arg.Any<RunnerIngestBatch>(), Arg.Any<CancellationToken>())
                .Returns(new RunnerIngestResult(RunnerIngestOutcome.Applied, 2));
            this.Findings.SubmitAsync(Arg.Any<RunnerCallContext>(), Arg.Any<RunnerFindingsChunk>(), Arg.Any<CancellationToken>())
                .Returns(RunnerSubmissionResult.Published());
            this._host = new HostBuilder().ConfigureWebHost(builder => builder
                .UseTestServer()
                .UseEnvironment("Testing")
                .ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(RunnerExecutionController).Assembly)
                        .AddJsonOptions(options => Program.ConfigureJsonSerialization(options.JsonSerializerOptions));
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestRunnerAuthenticationHandler>(RunnerAuthenticationDefaults.Scheme, _ => { });
                    services.AddAuthorization();
                    services.AddSingleton(Substitute.For<IRunnerToolProxy>());
                    services.AddSingleton(Substitute.For<IRunnerMemoryProxy>());
                    services.AddSingleton(Substitute.For<IRunnerAiRelay>());
                    services.AddSingleton(this.Ingest);
                    services.AddSingleton(this.Findings);
                    services.AddSingleton(this.PriorResults);
                    services.AddSingleton(Substitute.For<IRunnerWorkspaceServer>());
                    services.AddSingleton(Substitute.For<IGitUploadPackTransport>());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                })).Start();
            this.Server = this._host.GetTestServer();
            this.Client = this._host.GetTestClient();
        }

        public IRunnerIngestService Ingest { get; } = Substitute.For<IRunnerIngestService>();
        public IRunnerFindingsIntake Findings { get; } = Substitute.For<IRunnerFindingsIntake>();
        public IRunnerPriorResultsReader PriorResults { get; } = Substitute.For<IRunnerPriorResultsReader>();
        public JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
        public TestServer Server { get; }
        public HttpClient Client { get; }

        public void Dispose()
        {
            this.Client.Dispose();
            this._host.Dispose();
        }
    }

    private sealed class TestRunnerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(RunnerAuthenticationDefaults.RunnerIdClaim, JobId.ToString("D"))], this.Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), this.Scheme.Name)));
        }
    }
}
