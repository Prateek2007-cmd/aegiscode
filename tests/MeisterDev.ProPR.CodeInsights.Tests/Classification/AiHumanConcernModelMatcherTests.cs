// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Classification;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.CodeInsights.Tests.Classification;

public sealed class AiHumanConcernModelMatcherTests
{
    [Theory]
    [InlineData(
        "{\"state\":\"NoMatch\",\"matchedFindingIds\":[],\"equivalentConcernIds\":[],\"evaluatedCandidateIds\":[\"c1\"],\"confidence\":0.9,\"rationale\":\"Different issue\"}",
        HumanConcernMatchState.NoMatch)]
    [InlineData(
        "{\"state\":\"Match\",\"matchedFindingIds\":[\"c1\"],\"equivalentConcernIds\":[],\"evaluatedCandidateIds\":[\"c1\"],\"confidence\":0.9,\"rationale\":\"Same issue\"}",
        HumanConcernMatchState.Match)]
    [InlineData(
        "{\"state\":\"NoMatch\",\"matchedFindingIds\":[],\"equivalentConcernIds\":[],\"evaluatedCandidateIds\":[],\"confidence\":0.9}",
        HumanConcernMatchState.Unknown)]
    [InlineData(
        "{\"state\":\"Match\",\"matchedFindingIds\":[\"invented\"],\"equivalentConcernIds\":[],\"evaluatedCandidateIds\":[\"c1\"],\"confidence\":0.9}",
        HumanConcernMatchState.Unknown)]
    public async Task DecisionsRequireExplicitAllowedIdentitiesAndCompleteNegativeCoverage(string response, HumanConcernMatchState expected)
    {
        var client = Guid.NewGuid();
        var chat = Substitute.For<IChatClient>();
        var answer = new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>()).Returns(answer);
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chat);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(client, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>()).Returns(runtime);
        var usage = Substitute.For<IModelUsageRecorder>();
        var sut = new AiHumanConcernModelMatcher(resolver, usage, NullLogger<AiHumanConcernModelMatcher>.Instance);
        var result = await sut.MatchAsync(new(client, Guid.NewGuid(), "A verified human concern", [new("c1", "f.cs", 1, "A model candidate")]));
        Assert.Equal(expected, result.State);
        Assert.True(result.ModelWasAsked);
        await usage.Received(1).RecordAsync(client, runtime, answer, Arg.Any<CancellationToken>());
        await chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Is<ChatOptions>(options => options.MaxOutputTokens <= 2048), Arg.Any<CancellationToken>());
    }
}
