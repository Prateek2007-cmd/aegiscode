// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Support;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal static class ProviderSnapshotCommentIdentity
{
    public static Guid? FromLogin(string? login)
    {
        return string.IsNullOrWhiteSpace(login) ? null : StableGuidGenerator.Create(login);
    }
}
