using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionAuthoritySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "configuration_revision",
                table: "ai_connection_profiles",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "ai_usage_reports",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    execution_kind = table.Column<int>(type: "integer", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_attempt = table.Column<int>(type: "integer", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_revision = table.Column<long>(type: "bigint", nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    model_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    operation = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cached_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    reasoning_tokens = table.Column<long>(type: "bigint", nullable: false),
                    usage_incomplete = table.Column<bool>(type: "boolean", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_reports", x => x.sequence);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage_reports");

            migrationBuilder.DropColumn(
                name: "configuration_revision",
                table: "ai_connection_profiles");
        }
    }
}
