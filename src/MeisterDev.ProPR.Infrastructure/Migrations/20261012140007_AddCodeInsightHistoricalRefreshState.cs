using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeInsightHistoricalRefreshState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HistoricalRefreshNotBefore",
                table: "code_insight_pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHistoricalRefreshAttemptAt",
                table: "code_insight_pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastHistoricalRefreshAttemptId",
                table: "code_insight_pull_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHistoricalRefreshCompletedAt",
                table: "code_insight_pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastHistoricalRefreshReason",
                table: "code_insight_pull_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_historical_refresh_candidates",
                table: "code_insight_pull_requests",
                columns: new[] { "HistoricalRefreshNotBefore", "LastHistoricalRefreshAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_code_insight_historical_refresh_candidates",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "HistoricalRefreshNotBefore",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "LastHistoricalRefreshAttemptAt",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "LastHistoricalRefreshAttemptId",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "LastHistoricalRefreshCompletedAt",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "LastHistoricalRefreshReason",
                table: "code_insight_pull_requests");
        }
    }
}
