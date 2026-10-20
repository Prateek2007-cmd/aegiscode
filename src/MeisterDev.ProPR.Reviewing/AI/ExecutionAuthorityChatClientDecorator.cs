// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Runtime;
using MeisterDev.ProPR.Application.Features.ExecutionAuthority;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Contributes usage reporting to the registered execution authority in the provider pipeline's budget stage, the
///     stage the library reserves for host gating and metering. Inside the retry stage, so each attempt that returns
///     a response is reported once.
/// </summary>
/// <param name="scopes">The ambient execution scope the calls belong to.</param>
/// <param name="sink">Stores and reports the usage.</param>
/// <param name="connection">What the runtime was resolved for: the connection, its revision and its owner.</param>
/// <param name="timeProvider">The clock the duration and completion time are read from.</param>
public sealed class ExecutionAuthorityChatClientDecorator(
    IExecutionScopeAccessor scopes,
    IExecutionUsageSink sink,
    ExecutionAuthorityConnection connection,
    TimeProvider timeProvider) : IProviderChatClientDecorator
{
    /// <inheritdoc />
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Budget;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        return new ExecutionAuthorityChatClient(inner, scopes, sink, connection, timeProvider);
    }
}
