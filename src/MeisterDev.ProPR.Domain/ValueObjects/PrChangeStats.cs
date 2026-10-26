// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     Change statistics for a pull request, counted from the actual unified diffs of its changed
///     files — never from the AI's output, so the numbers on the published review cannot be invented.
/// </summary>
/// <param name="FilesChanged">Number of changed files in the pull request.</param>
/// <param name="LinesAdded">Added content lines across all changed files.</param>
/// <param name="LinesRemoved">Removed content lines across all changed files.</param>
public sealed record PrChangeStats(int FilesChanged, int LinesAdded, int LinesRemoved);
