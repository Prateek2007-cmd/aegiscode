// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Ports;

public enum ModelEvidenceRetryResult
{
    Retried,
    NotFound,
    Disabled,
    Busy,
    Cooldown,
    Unreconstructable
}

public sealed record ModelEvidenceRetryTarget(Guid AggregateId, string ProviderScope, Guid ConnectionId);

public interface IModelEvidenceMaintenance
{
    Task<int> SweepAsync(int maximumJobs, CancellationToken ct = default);
    Task<ModelEvidenceRetryResult> RetryAsync(ModelEvidenceRetryTarget target, IReadOnlyCollection<Guid> authorizedClients, CancellationToken ct = default);
}
