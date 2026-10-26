// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     One entry of a pull request walkthrough: what changed in a single file and why it matters.
///     Produced by the reviewing AI from its read of the actual diff; entries are only published for
///     files that are actually part of the pull request.
/// </summary>
/// <param name="FilePath">File path relative to the repository root.</param>
/// <param name="Description">One short sentence: what changed and why it matters.</param>
public sealed record PrWalkthroughEntry(string FilePath, string Description);
