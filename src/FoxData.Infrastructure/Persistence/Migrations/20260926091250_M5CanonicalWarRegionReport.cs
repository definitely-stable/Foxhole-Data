using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5CanonicalWarRegionReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "runtime");

            migrationBuilder.CreateTable(
                name: "normalization_runs",
                schema: "evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_parse_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalizer_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_normalization_runs", x => x.id);
                    table.CheckConstraint("ck_normalization_runs_completed_after_started", "completed_at >= started_at");
                    table.CheckConstraint("ck_normalization_runs_outcome", "outcome IN ('normalized','rejected','failed')");
                    table.ForeignKey(
                        name: "FK_normalization_runs_source_parse_runs_source_parse_run_id",
                        column: x => x.source_parse_run_id,
                        principalSchema: "evidence",
                        principalTable: "source_parse_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "regions",
                schema: "runtime",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_regions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wars",
                schema: "runtime",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shard_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_war_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    war_number = table.Column<int>(type: "integer", nullable: true),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wars", x => x.id);
                    table.CheckConstraint("ck_wars_observation_window", "last_observed_at >= first_observed_at");
                    table.ForeignKey(
                        name: "FK_wars_shards_shard_id",
                        column: x => x.shard_id,
                        principalSchema: "sources",
                        principalTable: "shards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "war_observations",
                schema: "runtime",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalization_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    representation_fetch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    war_number = table.Column<int>(type: "integer", nullable: true),
                    winner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    conquest_start_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    conquest_end_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resistance_start_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduled_conquest_end_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    required_victory_towns = table.Column<int>(type: "integer", nullable: true),
                    short_required_victory_towns = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_war_observations", x => x.id);
                    table.CheckConstraint("ck_war_observations_required_victory_towns", "required_victory_towns IS NULL OR required_victory_towns >= 0");
                    table.CheckConstraint("ck_war_observations_short_required_victory_towns", "short_required_victory_towns IS NULL OR short_required_victory_towns >= 0");
                    table.ForeignKey(
                        name: "FK_war_observations_fetches_representation_fetch_id",
                        column: x => x.representation_fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_war_observations_normalization_runs_normalization_run_id",
                        column: x => x.normalization_run_id,
                        principalSchema: "evidence",
                        principalTable: "normalization_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_war_observations_wars_war_id",
                        column: x => x.war_id,
                        principalSchema: "runtime",
                        principalTable: "wars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "war_regions",
                schema: "runtime",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    region_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_map_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_region_id = table.Column<int>(type: "integer", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_war_regions", x => x.id);
                    table.CheckConstraint("ck_war_regions_observation_window", "last_seen_at >= first_seen_at");
                    table.ForeignKey(
                        name: "FK_war_regions_regions_region_id",
                        column: x => x.region_id,
                        principalSchema: "runtime",
                        principalTable: "regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_war_regions_wars_war_id",
                        column: x => x.war_id,
                        principalSchema: "runtime",
                        principalTable: "wars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "war_report_observations",
                schema: "runtime",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    war_region_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalization_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    representation_fetch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    total_enlistments = table.Column<long>(type: "bigint", nullable: true),
                    colonial_casualties = table.Column<long>(type: "bigint", nullable: true),
                    warden_casualties = table.Column<long>(type: "bigint", nullable: true),
                    day_of_war = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_war_report_observations", x => x.id);
                    table.CheckConstraint("ck_war_report_observations_colonial_casualties", "colonial_casualties IS NULL OR colonial_casualties >= 0");
                    table.CheckConstraint("ck_war_report_observations_day_of_war", "day_of_war IS NULL OR day_of_war >= 0");
                    table.CheckConstraint("ck_war_report_observations_total_enlistments", "total_enlistments IS NULL OR total_enlistments >= 0");
                    table.CheckConstraint("ck_war_report_observations_warden_casualties", "warden_casualties IS NULL OR warden_casualties >= 0");
                    table.ForeignKey(
                        name: "FK_war_report_observations_fetches_representation_fetch_id",
                        column: x => x.representation_fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_war_report_observations_normalization_runs_normalization_ru~",
                        column: x => x.normalization_run_id,
                        principalSchema: "evidence",
                        principalTable: "normalization_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_war_report_observations_war_regions_war_region_id",
                        column: x => x.war_region_id,
                        principalSchema: "runtime",
                        principalTable: "war_regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_normalization_runs_outcome_created",
                schema: "evidence",
                table: "normalization_runs",
                columns: new[] { "outcome", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_normalization_runs_parse_normalizer",
                schema: "evidence",
                table: "normalization_runs",
                columns: new[] { "source_parse_run_id", "normalizer_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_regions_canonical_key",
                schema: "runtime",
                table: "regions",
                column: "canonical_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_war_observations_representation_fetch_id",
                schema: "runtime",
                table: "war_observations",
                column: "representation_fetch_id");

            migrationBuilder.CreateIndex(
                name: "ix_war_observations_war_observed",
                schema: "runtime",
                table: "war_observations",
                columns: new[] { "war_id", "observed_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_war_observations_normalization_run",
                schema: "runtime",
                table: "war_observations",
                column: "normalization_run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_war_regions_region_id",
                schema: "runtime",
                table: "war_regions",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "ix_war_regions_war_region",
                schema: "runtime",
                table: "war_regions",
                columns: new[] { "war_id", "region_id" });

            migrationBuilder.CreateIndex(
                name: "ux_war_regions_war_source_map",
                schema: "runtime",
                table: "war_regions",
                columns: new[] { "war_id", "source_map_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_war_report_observations_region_observed",
                schema: "runtime",
                table: "war_report_observations",
                columns: new[] { "war_region_id", "observed_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_war_report_observations_representation_fetch_id",
                schema: "runtime",
                table: "war_report_observations",
                column: "representation_fetch_id");

            migrationBuilder.CreateIndex(
                name: "ux_war_report_observations_normalization_run",
                schema: "runtime",
                table: "war_report_observations",
                column: "normalization_run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wars_shard_war_number",
                schema: "runtime",
                table: "wars",
                columns: new[] { "shard_id", "war_number" });

            migrationBuilder.CreateIndex(
                name: "ux_wars_shard_source_war",
                schema: "runtime",
                table: "wars",
                columns: new[] { "shard_id", "source_war_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "war_observations",
                schema: "runtime");

            migrationBuilder.DropTable(
                name: "war_report_observations",
                schema: "runtime");

            migrationBuilder.DropTable(
                name: "normalization_runs",
                schema: "evidence");

            migrationBuilder.DropTable(
                name: "war_regions",
                schema: "runtime");

            migrationBuilder.DropTable(
                name: "regions",
                schema: "runtime");

            migrationBuilder.DropTable(
                name: "wars",
                schema: "runtime");
        }
    }
}
