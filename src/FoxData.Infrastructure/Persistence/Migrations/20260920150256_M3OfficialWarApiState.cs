using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3OfficialWarApiState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "endpoint_poll_state",
                schema: "ingest",
                columns: table => new
                {
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_processed_fetch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    latest_validation_fetch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    representation_fetch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    validator_etag = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    source_cache_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_target_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_http_response_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    policy_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_poll_state", x => x.endpoint_id);
                    table.CheckConstraint("ck_endpoint_poll_state_consecutive_failures", "consecutive_failures >= 0");
                    table.ForeignKey(
                        name: "FK_endpoint_poll_state_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "sources",
                        principalTable: "endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_endpoint_poll_state_fetches_last_processed_fetch_id",
                        column: x => x.last_processed_fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_endpoint_poll_state_fetches_latest_validation_fetch_id",
                        column: x => x.latest_validation_fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_endpoint_poll_state_fetches_representation_fetch_id",
                        column: x => x.representation_fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_parse_runs",
                schema: "evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    representation_fetch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    adapter_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    parser_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    fingerprint_algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    structural_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    unknown_property_count = table.Column<int>(type: "integer", nullable: false),
                    unknown_code_count = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_parse_runs", x => x.id);
                    table.CheckConstraint("ck_source_parse_runs_completed_after_started", "completed_at >= started_at");
                    table.CheckConstraint("ck_source_parse_runs_unknown_code_count", "unknown_code_count >= 0");
                    table.CheckConstraint("ck_source_parse_runs_unknown_property_count", "unknown_property_count >= 0");
                    table.ForeignKey(
                        name: "FK_source_parse_runs_fetches_representation_fetch_id",
                        column: x => x.representation_fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_poll_state_last_processed_fetch_id",
                schema: "ingest",
                table: "endpoint_poll_state",
                column: "last_processed_fetch_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_poll_state_latest_validation_fetch_id",
                schema: "ingest",
                table: "endpoint_poll_state",
                column: "latest_validation_fetch_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_poll_state_representation_fetch_id",
                schema: "ingest",
                table: "endpoint_poll_state",
                column: "representation_fetch_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_parse_runs_outcome_created",
                schema: "evidence",
                table: "source_parse_runs",
                columns: new[] { "outcome", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_source_parse_runs_representation_capability_parser",
                schema: "evidence",
                table: "source_parse_runs",
                columns: new[] { "representation_fetch_id", "capability_key", "parser_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "endpoint_poll_state",
                schema: "ingest");

            migrationBuilder.DropTable(
                name: "source_parse_runs",
                schema: "evidence");
        }
    }
}
