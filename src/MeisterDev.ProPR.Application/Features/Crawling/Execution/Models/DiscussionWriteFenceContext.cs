// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;

public sealed record DiscussionWriteFence(Guid SourceId, Guid Token);

/// <summary>Propagates a discussion write fence through awaited consumer database operations.</summary>
public static class DiscussionWriteFenceContext
{
    private static readonly AsyncLocal<DiscussionWriteFence?> Slot = new();
    public static DiscussionWriteFence? Current => Slot.Value;
    public static IDisposable Begin(DiscussionWriteFence? fence) => new Scope(fence);

    private sealed class Scope : IDisposable
    {
        private readonly DiscussionWriteFence? _previous = Slot.Value;

        public Scope(DiscussionWriteFence? fence)
        {
            Slot.Value = fence;
        }

        public void Dispose()
        {
            Slot.Value = this._previous;
        }
    }
}
