// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightHumanConcernConfiguration : IEntityTypeConfiguration<CodeInsightHumanConcern>
{
    public void Configure(EntityTypeBuilder<CodeInsightHumanConcern> builder)
    {
        builder.ToTable("code_insight_human_concerns");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.ProviderScope).HasMaxLength(1024);
        builder.Property(row => row.NativeIdentity).HasMaxLength(64);
        builder.Property(row => row.ProviderThreadId).HasMaxLength(256);
        builder.Property(row => row.SourceFingerprint).HasMaxLength(64);
        builder.Property(row => row.EquivalenceProof).HasMaxLength(64);
        builder.Property(row => row.ClassifierVersion).HasMaxLength(256);
        builder.Property(row => row.DimensionClassifierVersion).HasMaxLength(256);
        builder.Property(row => row.UnavailableReason).HasMaxLength(128);
        builder.Property(row => row.TypeMembership).HasMaxLength(1024);
        builder.Property(row => row.Qualifier).HasConversion<short?>();
        builder.Property(row => row.Anchor).HasColumnType("jsonb")
            .HasConversion(
                value => value == null ? null : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                json => json == null ? null : JsonSerializer.Deserialize<ReviewConcernAnchor>(json, (JsonSerializerOptions?)null));
        builder.HasIndex(row => row.NativeIdentity).IsUnique();
        builder.HasIndex(row => new { row.CodeInsightPullRequestId, row.ObservedAt });
        builder.HasOne<CodeInsightPullRequest>().WithMany().HasForeignKey(row => row.CodeInsightPullRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
