// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>Orders shared evidence writes across connection-specific discussion checkpoints.</summary>
public sealed class DiscussionObservationFenceState
{
    public Guid Id { get; set; }
    public Guid OwnerStateId { get; set; }
    public Guid Token { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public bool Completed { get; set; }
}
