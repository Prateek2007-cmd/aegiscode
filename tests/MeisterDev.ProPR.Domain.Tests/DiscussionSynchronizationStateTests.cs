// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Domain.Tests;

public sealed class DiscussionSynchronizationStateTests
{
    [Fact]
    public void ConsumerAcknowledgmentsAreIndependentAndVersioned()
    {
        var progress = new DiscussionThreadProgress();
        Assert.True(progress.Needs("archive", "source", "v1"));
        progress.Acknowledge("archive", "source", "v1");
        Assert.False(progress.Needs("archive", "source", "v1"));
        Assert.True(progress.Needs("miss", "source", "v1"));
        Assert.True(progress.Needs("archive", "edited", "v1"));
        Assert.True(progress.Needs("archive", "source", "v2"));
    }
}
