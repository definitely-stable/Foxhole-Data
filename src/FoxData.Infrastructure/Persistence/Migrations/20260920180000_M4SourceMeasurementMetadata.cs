using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4SourceMeasurementMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "source_version",
                schema: "evidence",
                table: "source_parse_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_last_updated",
                schema: "evidence",
                table: "source_parse_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "decoded_byte_length",
                schema: "evidence",
                table: "source_parse_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_parse_runs_decoded_byte_length",
                schema: "evidence",
                table: "source_parse_runs",
                sql: "decoded_byte_length IS NULL OR decoded_byte_length >= 0");

            migrationBuilder.CreateTable(
                name: "source_schedule_decisions",
                schema: "evidence",
                columns: table => new
                {
                    fetch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    effective_cadence_ms = table.Column<long>(type: "bigint", nullable: false),
                    endpoint_active = table.Column<bool>(type: "boolean", nullable: false),
                    probe_selected = table.Column<bool>(type: "boolean", nullable: false),
                    source_cache_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_target_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    successor_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    successor_available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_schedule_decisions", x => x.fetch_id);
                    table.CheckConstraint(
                        "ck_source_schedule_decisions_effective_cadence_ms",
                        "effective_cadence_ms > 0");
                    table.ForeignKey(
                        name: "FK_source_schedule_decisions_collection_jobs_successor_job_id",
                        column: x => x.successor_job_id,
                        principalSchema: "ingest",
                        principalTable: "collection_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_schedule_decisions_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "sources",
                        principalTable: "endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_source_schedule_decisions_fetches_fetch_id",
                        column: x => x.fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_schedule_decisions_endpoint",
                schema: "evidence",
                table: "source_schedule_decisions",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "ux_source_schedule_decisions_successor_job",
                schema: "evidence",
                table: "source_schedule_decisions",
                column: "successor_job_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_schedule_decisions",
                schema: "evidence");

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_parse_runs_decoded_byte_length",
                schema: "evidence",
                table: "source_parse_runs");

            migrationBuilder.DropColumn(
                name: "decoded_byte_length",
                schema: "evidence",
                table: "source_parse_runs");

            migrationBuilder.DropColumn(
                name: "source_version",
                schema: "evidence",
                table: "source_parse_runs");

            migrationBuilder.DropColumn(
                name: "source_last_updated",
                schema: "evidence",
                table: "source_parse_runs");
        }
    }
}
