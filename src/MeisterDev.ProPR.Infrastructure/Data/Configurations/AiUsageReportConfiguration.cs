// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

/// <summary>EF mapping for <see cref="AiUsageReportRecord" />, the usage reports kept for an execution authority.</summary>
public sealed class AiUsageReportConfiguration : IEntityTypeConfiguration<AiUsageReportRecord>
{
    public void Configure(EntityTypeBuilder<AiUsageReportRecord> builder)
    {
        builder.ToTable("ai_usage_reports");
        builder.HasKey(r => r.Sequence);

        // The identity column is the feed's cursor.
        builder.Property(r => r.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
        builder.Property(r => r.ExecutionKind).HasColumnName("execution_kind");
        builder.Property(r => r.ExecutionId).HasColumnName("execution_id");
        builder.Property(r => r.ExecutionAttempt).HasColumnName("execution_attempt");
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.ClientId).HasColumnName("client_id");
        builder.Property(r => r.ConnectionId).HasColumnName("connection_id").IsRequired();
        builder.Property(r => r.ConnectionRevision).HasColumnName("connection_revision").IsRequired();
        builder.Property(r => r.ProviderKind).HasColumnName("provider_kind").HasMaxLength(128).IsRequired();
        builder.Property(r => r.ModelId).HasColumnName("model_id").HasMaxLength(512).IsRequired();
        builder.Property(r => r.Operation).HasColumnName("operation").IsRequired();
        builder.Property(r => r.InputTokens).HasColumnName("input_tokens").IsRequired();
        builder.Property(r => r.OutputTokens).HasColumnName("output_tokens").IsRequired();
        builder.Property(r => r.CachedInputTokens).HasColumnName("cached_input_tokens").IsRequired();
        builder.Property(r => r.CacheWriteTokens).HasColumnName("cache_write_tokens").IsRequired();
        builder.Property(r => r.ReasoningTokens).HasColumnName("reasoning_tokens").IsRequired();
        builder.Property(r => r.UsageIncomplete).HasColumnName("usage_incomplete").IsRequired();
        builder.Property(r => r.DurationMilliseconds).HasColumnName("duration_ms").IsRequired();
        builder.Property(r => r.CompletedAt).HasColumnName("completed_at").IsRequired();
        builder.Property(r => r.RecordedAt).HasColumnName("recorded_at").IsRequired();
    }
}
