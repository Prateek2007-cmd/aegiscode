// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class CodeInsightModelConcernEvidenceConfiguration : IEntityTypeConfiguration<CodeInsightModelConcernEvidence>
{
    public void Configure(EntityTypeBuilder<CodeInsightModelConcernEvidence> builder)
    {
        builder.ToTable("code_insight_model_concern_evidence");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.ModelId).HasMaxLength(256);
        builder.Property(row => row.LogicalModelName).HasMaxLength(128);
        builder.Property(row => row.SourceFingerprint).HasMaxLength(64);
        builder.Property(row => row.CandidateSetFingerprint).HasMaxLength(64);
        builder.Property(row => row.ClassifierVersion).HasMaxLength(256);
        builder.Property(row => row.Decision).HasMaxLength(32);
        builder.Property(row => row.UnavailableReason).HasMaxLength(128);
        builder.Property(row => row.Rationale).HasMaxLength(300);
        builder.Property(row => row.EquivalentConcernIds).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<IReadOnlyList<string>>(json, (JsonSerializerOptions?)null) ?? new string[0],
                new ValueComparer<IReadOnlyList<string>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    ids => ids.Aggregate(0, (hash, id) => HashCode.Combine(hash, id.GetHashCode())), ids => ids.ToArray()));
        builder.Property(row => row.CandidateIds).HasColumnType("jsonb")
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<IReadOnlyList<string>>(json, (JsonSerializerOptions?)null) ?? new string[0],
                new ValueComparer<IReadOnlyList<string>>(
                    (a, b) => a != null && b != null && a.SequenceEqual(b),
                    ids => ids.Aggregate(0, (hash, id) => HashCode.Combine(hash, id.GetHashCode())), ids => ids.ToArray()));
        builder.HasIndex(row => new { row.HumanConcernId, row.ModelId, row.LogicalModelName }).IsUnique();
        builder.HasOne<CodeInsightHumanConcern>().WithMany().HasForeignKey(row => row.HumanConcernId).OnDelete(DeleteBehavior.Cascade);
    }
}
