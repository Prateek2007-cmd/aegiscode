// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class DiscussionObservationFenceStateConfiguration : IEntityTypeConfiguration<DiscussionObservationFenceState>
{
    public void Configure(EntityTypeBuilder<DiscussionObservationFenceState> builder)
    {
        builder.ToTable("discussion_observation_fences");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(row => row.OwnerStateId).HasColumnName("owner_state_id");
        builder.Property(row => row.Token).HasColumnName("token");
        builder.Property(row => row.ExpiresAt).HasColumnName("expires_at");
        builder.Property(row => row.ObservedAt).HasColumnName("observed_at");
        builder.Property(row => row.Completed).HasColumnName("completed");
    }
}
