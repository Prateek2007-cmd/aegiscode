// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class SecurityScanRecordConfiguration : IEntityTypeConfiguration<SecurityScanRecord>
{
    public void Configure(EntityTypeBuilder<SecurityScanRecord> builder)
    {
        builder.ToTable("security_scan_records");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.ClientId).HasColumnName("client_id").IsRequired();
        builder.HasIndex(r => new { r.ClientId, r.QueuedAt });

        builder.Property(r => r.RepositoryUrl)
            .HasColumnName("repository_url")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(r => r.Ref).HasColumnName("ref").HasMaxLength(256);

        builder.Property(r => r.Status).HasColumnName("status").IsRequired();
        builder.Property(r => r.ProgressPct).HasColumnName("progress_pct").IsRequired();

        builder.Property(r => r.QueuedAt).HasColumnName("queued_at").IsRequired();
        builder.Property(r => r.StartedAt).HasColumnName("started_at");
        builder.Property(r => r.FinishedAt).HasColumnName("finished_at");

        builder.Property(r => r.Error).HasColumnName("error").HasMaxLength(2048);
        builder.Property(r => r.EngineVersion).HasColumnName("engine_version").HasMaxLength(64);
        builder.Property(r => r.ScannerVersionsJson).HasColumnName("scanner_versions_json");
        builder.Property(r => r.CoverageJson).HasColumnName("coverage_json");
        builder.Property(r => r.SbomPath).HasColumnName("sbom_path").HasMaxLength(512);

        builder.Property(r => r.CriticalCount).HasColumnName("critical_count").IsRequired();
        builder.Property(r => r.HighCount).HasColumnName("high_count").IsRequired();
        builder.Property(r => r.MediumCount).HasColumnName("medium_count").IsRequired();
        builder.Property(r => r.LowCount).HasColumnName("low_count").IsRequired();
        builder.Property(r => r.InfoCount).HasColumnName("info_count").IsRequired();

        builder.Property(r => r.CreatedByUserId).HasColumnName("created_by_user_id");
    }
}
