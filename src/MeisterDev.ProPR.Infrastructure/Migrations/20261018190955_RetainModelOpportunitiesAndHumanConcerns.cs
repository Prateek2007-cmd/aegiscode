using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetainModelOpportunitiesAndHumanConcerns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "model_evidence_manifest_json",
                table: "review_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_evidence_json",
                table: "review_file_results",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "concern_anchor_json",
                table: "retained_threads",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ModelConcernConnectionId",
                table: "code_insight_harvest_coverage",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ModelConcernEnumerationComplete",
                table: "code_insight_harvest_coverage",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ModelConcernObservedAt",
                table: "code_insight_harvest_coverage",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "code_insight_human_concerns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeInsightPullRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderScope = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NativeIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderThreadId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CanonicalConcernId = table.Column<Guid>(type: "uuid", nullable: false),
                    EquivalenceProof = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Anchor = table.Column<string>(type: "jsonb", nullable: true),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EncryptedDiscussion = table.Column<string>(type: "text", nullable: false),
                    RetainedSourceComplete = table.Column<bool>(type: "boolean", nullable: false),
                    HarvestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsSubstantive = table.Column<bool>(type: "boolean", nullable: false),
                    IsInScope = table.Column<bool>(type: "boolean", nullable: false),
                    WasActedOn = table.Column<bool>(type: "boolean", nullable: false),
                    ThreadResolved = table.Column<bool>(type: "boolean", nullable: false),
                    JudgementFailed = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    ClassifierVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClassifierConfidence = table.Column<double>(type: "double precision", nullable: true),
                    IsHumanConfirmed = table.Column<bool>(type: "boolean", nullable: true),
                    IsSingleConcern = table.Column<bool>(type: "boolean", nullable: true),
                    UnavailableReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DimensionClassifierVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FailedDimensionAttempts = table.Column<int>(type: "integer", nullable: false),
                    TypeMembership = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Qualifier = table.Column<short>(type: "smallint", nullable: true),
                    DimensionJudgementFailed = table.Column<bool>(type: "boolean", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastModelEvidenceRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_human_concerns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_insight_human_concerns_code_insight_pull_requests_Code~",
                        column: x => x.CodeInsightPullRequestId,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_model_concern_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HumanConcernId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LogicalModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CandidateSetFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClassifierVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CandidateIds = table.Column<string>(type: "jsonb", nullable: false),
                    EquivalentConcernIds = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Rationale = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnavailableReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_model_concern_evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_insight_model_concern_evidence_code_insight_human_conc~",
                        column: x => x.HumanConcernId,
                        principalTable: "code_insight_human_concerns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_human_concerns_CodeInsightPullRequestId_Observ~",
                table: "code_insight_human_concerns",
                columns: new[] { "CodeInsightPullRequestId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_human_concerns_NativeIdentity",
                table: "code_insight_human_concerns",
                column: "NativeIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_model_concern_evidence_HumanConcernId_ModelId_~",
                table: "code_insight_model_concern_evidence",
                columns: new[] { "HumanConcernId", "ModelId", "LogicalModelName" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER reviewer_performance_human_concerns_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_human_concerns
                    FOR EACH ROW EXECUTE FUNCTION mark_reviewer_performance_dirty('CodeInsightPullRequestId');
                CREATE FUNCTION mark_model_concern_performance_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE source_row jsonb;
                BEGIN
                    source_row := CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END;
                    INSERT INTO code_insight_performance_dirty ("Id", "CodeInsightPullRequestId")
                    SELECT gen_random_uuid(), concern."CodeInsightPullRequestId" FROM code_insight_human_concerns concern
                    WHERE concern."Id" = (source_row ->> 'HumanConcernId')::uuid;
                    RETURN NULL;
                END;
                $$;
                CREATE TRIGGER reviewer_performance_model_concerns_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_model_concern_evidence
                    FOR EACH ROW EXECUTE FUNCTION mark_model_concern_performance_dirty();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER reviewer_performance_model_concerns_dirty ON code_insight_model_concern_evidence;
                DROP FUNCTION mark_model_concern_performance_dirty();
                DROP TRIGGER reviewer_performance_human_concerns_dirty ON code_insight_human_concerns;
                """);
            migrationBuilder.DropTable(
                name: "code_insight_model_concern_evidence");

            migrationBuilder.DropTable(
                name: "code_insight_human_concerns");

            migrationBuilder.DropColumn(
                name: "model_evidence_manifest_json",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "model_evidence_json",
                table: "review_file_results");

            migrationBuilder.DropColumn(
                name: "concern_anchor_json",
                table: "retained_threads");

            migrationBuilder.DropColumn(
                name: "ModelConcernConnectionId",
                table: "code_insight_harvest_coverage");

            migrationBuilder.DropColumn(
                name: "ModelConcernEnumerationComplete",
                table: "code_insight_harvest_coverage");

            migrationBuilder.DropColumn(
                name: "ModelConcernObservedAt",
                table: "code_insight_harvest_coverage");
        }
    }
}
