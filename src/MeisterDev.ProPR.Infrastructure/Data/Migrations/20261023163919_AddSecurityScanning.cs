using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_scan_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(256)", maxLength: 256, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    progress_pct = table.Column<int>(type: "integer", nullable: false),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    engine_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scanner_versions_json = table.Column<string>(type: "text", nullable: true),
                    coverage_json = table.Column<string>(type: "text", nullable: true),
                    sbom_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    critical_count = table.Column<int>(type: "integer", nullable: false),
                    high_count = table.Column<int>(type: "integer", nullable: false),
                    medium_count = table.Column<int>(type: "integer", nullable: false),
                    low_count = table.Column<int>(type: "integer", nullable: false),
                    info_count = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_scan_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "security_finding_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    scanner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    priority_score = table.Column<int>(type: "integer", nullable: false),
                    score_gaps_json = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    line = table.Column<int>(type: "integer", nullable: true),
                    evidence = table.Column<string>(type: "text", nullable: true),
                    remediation = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    first_seen_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    suppressions_json = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_finding_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_security_finding_records_security_scan_records_scan_id",
                        column: x => x.scan_id,
                        principalTable: "security_scan_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_finding_records_client_id_fingerprint",
                table: "security_finding_records",
                columns: new[] { "client_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_finding_records_scan_id",
                table: "security_finding_records",
                column: "scan_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_finding_records_scan_id_severity",
                table: "security_finding_records",
                columns: new[] { "scan_id", "severity" });

            migrationBuilder.CreateIndex(
                name: "IX_security_scan_records_client_id_queued_at",
                table: "security_scan_records",
                columns: new[] { "client_id", "queued_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_finding_records");

            migrationBuilder.DropTable(
                name: "security_scan_records");
        }
    }
}
