// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class SecurityFindingRecordConfiguration : IEntityTypeConfiguration<SecurityFindingRecord>
{
    public void Configure(EntityTypeBuilder<SecurityFindingRecord> builder)
    {
        builder.ToTable("security_finding_records");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.ClientId).HasColumnName("client_id").IsRequired();
        builder.Property(r => r.ScanId).HasColumnName("scan_id").IsRequired();
        builder.HasIndex(r => r.ScanId);

        builder.Property(r => r.Fingerprint)
            .HasColumnName("fingerprint")
            .HasMaxLength(128)
            .IsRequired();
        builder.HasIndex(r => new { r.ClientId, r.Fingerprint }).IsUnique();

        builder.Property(r => r.Scanner).HasColumnName("scanner").HasMaxLength(64).IsRequired();
        builder.Property(r => r.RuleId).HasColumnName("rule_id").HasMaxLength(128);
        builder.Property(r => r.Severity).HasColumnName("severity").IsRequired();
        builder.HasIndex(r => new { r.ScanId, r.Severity });

        builder.Property(r => r.PriorityScore).HasColumnName("priority_score").IsRequired();
        builder.Property(r => r.ScoreGapsJson).HasColumnName("score_gaps_json");

        builder.Property(r => r.Title).HasColumnName("title").HasMaxLength(512).IsRequired();
        builder.Property(r => r.Category).HasColumnName("category").HasMaxLength(64).IsRequired();
        builder.Property(r => r.FilePath).HasColumnName("file_path").HasMaxLength(1024);
        builder.Property(r => r.Line).HasColumnName("line");
        builder.Property(r => r.Evidence).HasColumnName("evidence");
        builder.Property(r => r.Remediation).HasColumnName("remediation");
        builder.Property(r => r.Confidence).HasColumnName("confidence").HasMaxLength(16).IsRequired();

        builder.Property(r => r.Status).HasColumnName("status").IsRequired();
        builder.Property(r => r.FirstSeenScanId).HasColumnName("first_seen_scan_id").IsRequired();
        builder.Property(r => r.LastSeenScanId).HasColumnName("last_seen_scan_id").IsRequired();
        builder.Property(r => r.FirstSeenAt).HasColumnName("first_seen_at").IsRequired();
        builder.Property(r => r.LastSeenAt).HasColumnName("last_seen_at").IsRequired();
        builder.Property(r => r.SuppressionsJson).HasColumnName("suppressions_json");

        builder.HasOne(r => r.Scan)
            .WithMany()
            .HasForeignKey(r => r.ScanId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
