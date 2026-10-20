// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Extensibility.Execution;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.ExecutionAuthority;

public sealed class ExtensibilityContractTests
{
    [Fact]
    public void TheContractsAssembly_ReferencesNoOtherProPRAssembly()
    {
        // An implementation of these contracts compiles against this assembly alone, so it cannot depend on ProPR's
        // internals.
        var references = typeof(IExecutionAuthority).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("MeisterDev.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(references);
    }
}
