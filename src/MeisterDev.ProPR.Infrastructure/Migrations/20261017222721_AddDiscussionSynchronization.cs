using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscussionSynchronization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "body_fingerprint",
                table: "retained_thread_comments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discussion_synchronization_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    provider_scope_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    provider_project_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    requested_generation = table.Column<long>(type: "bigint", nullable: false),
                    completed_generation = table.Column<long>(type: "bigint", nullable: false),
                    observation_sequence = table.Column<long>(type: "bigint", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_full_reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_review_revision = table.Column<string>(type: "text", nullable: true),
                    retention_generation = table.Column<long>(type: "bigint", nullable: false),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false),
                    full_refresh_requested = table.Column<bool>(type: "boolean", nullable: false),
                    pending_thread_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    thread_progress_json = table.Column<string>(type: "jsonb", nullable: false),
                    reviewer_summaries_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discussion_synchronization_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discussion_sync_due",
                table: "discussion_synchronization_states",
                columns: new[] { "next_attempt_at", "is_closed" });

            migrationBuilder.CreateIndex(
                name: "uq_discussion_sync_scope",
                table: "discussion_synchronization_states",
                columns: new[] { "client_id", "connection_id", "provider", "provider_scope_path", "provider_project_key", "repository_id", "pull_request_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discussion_synchronization_states");

            migrationBuilder.DropColumn(
                name: "body_fingerprint",
                table: "retained_thread_comments");
        }
    }
}
