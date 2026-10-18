// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class DiscussionSynchronizationStateConfiguration : IEntityTypeConfiguration<DiscussionSynchronizationState>
{
    public void Configure(EntityTypeBuilder<DiscussionSynchronizationState> builder)
    {
        builder.ToTable("discussion_synchronization_states");
        builder.HasKey(state => state.Id);
        foreach (var property in typeof(DiscussionSynchronizationState).GetProperties())
        {
            builder.Property(property.Name).HasColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
        }

        builder.Property(state => state.Id).ValueGeneratedNever();
        builder.Property(state => state.ProviderScopePath).HasMaxLength(2048);
        builder.Property(state => state.ProviderProjectKey).HasMaxLength(512);
        builder.Property(state => state.RepositoryId).HasMaxLength(512);
        builder.Property(state => state.ThreadProgressJson).HasColumnType("jsonb");
        builder.Property(state => state.PendingThreadIdsJson).HasColumnType("jsonb");
        builder.Property(state => state.ReviewerSummariesJson).HasColumnType("jsonb");
        builder.HasIndex(state => new { state.NextAttemptAt, state.IsClosed }).HasDatabaseName("ix_discussion_sync_due");
        builder.HasIndex(state => new
        {
            state.ClientId,
            state.ConnectionId,
            state.Provider,
            state.ProviderScopePath,
            state.ProviderProjectKey,
            state.RepositoryId,
            state.PullRequestId
        }).IsUnique().HasDatabaseName("uq_discussion_sync_scope");
    }
}
