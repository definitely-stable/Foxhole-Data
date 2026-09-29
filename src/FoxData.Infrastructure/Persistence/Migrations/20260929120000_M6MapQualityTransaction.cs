using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FoxDataDbContext))]
[Migration("20260929120000_M6MapQualityTransaction")]
public sealed class M6MapQualityTransaction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE quality.map_quality_findings
                ADD COLUMN map_snapshot_id uuid NULL;

            UPDATE quality.map_quality_findings AS finding
            SET map_snapshot_id = quality_run.map_snapshot_id
            FROM quality.map_quality_runs AS quality_run
            WHERE quality_run.id = finding.quality_run_id;

            ALTER TABLE quality.map_quality_findings
                ALTER COLUMN map_snapshot_id SET NOT NULL;

            ALTER TABLE quality.map_quality_findings
                ADD CONSTRAINT ck_map_quality_findings_effect
                    CHECK (effect IN (
                        'informational',
                        'suspect',
                        'quarantined'));

            CREATE UNIQUE INDEX ux_map_quality_runs_id_snapshot
                ON quality.map_quality_runs
                    (id, map_snapshot_id);

            CREATE UNIQUE INDEX ux_map_quality_runs_observation_decision
                ON quality.map_quality_runs
                    (id, map_snapshot_id, war_region_id,
                     validation_fetch_id, decision);

            CREATE UNIQUE INDEX ux_map_item_occurrences_id_snapshot
                ON evidence.map_item_occurrences
                    (id, map_snapshot_id);

            CREATE UNIQUE INDEX ux_map_text_occurrences_id_snapshot
                ON evidence.map_text_occurrences
                    (id, map_snapshot_id);

            ALTER TABLE quality.map_quality_findings
                ADD CONSTRAINT "FK_map_quality_findings_quality_run_snapshot"
                    FOREIGN KEY (quality_run_id, map_snapshot_id)
                    REFERENCES quality.map_quality_runs
                        (id, map_snapshot_id)
                    ON DELETE RESTRICT,
                ADD CONSTRAINT "FK_map_quality_findings_item_snapshot"
                    FOREIGN KEY (
                        map_item_occurrence_id,
                        map_snapshot_id)
                    REFERENCES evidence.map_item_occurrences
                        (id, map_snapshot_id)
                    ON DELETE RESTRICT,
                ADD CONSTRAINT "FK_map_quality_findings_text_snapshot"
                    FOREIGN KEY (
                        map_text_occurrence_id,
                        map_snapshot_id)
                    REFERENCES evidence.map_text_occurrences
                        (id, map_snapshot_id)
                    ON DELETE RESTRICT;

            ALTER TABLE runtime.map_observations
                ADD COLUMN quality_decision character varying(32)
                    NOT NULL DEFAULT 'accepted';

            ALTER TABLE runtime.map_observations
                ADD CONSTRAINT ck_map_observations_quality_decision
                    CHECK (quality_decision = 'accepted'),
                ADD CONSTRAINT "FK_map_observations_quality_run_decision"
                    FOREIGN KEY (
                        quality_run_id,
                        map_snapshot_id,
                        war_region_id,
                        validation_fetch_id,
                        quality_decision)
                    REFERENCES quality.map_quality_runs (
                        id,
                        map_snapshot_id,
                        war_region_id,
                        validation_fetch_id,
                        decision)
                    ON DELETE RESTRICT;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE runtime.map_observations
                DROP CONSTRAINT IF EXISTS
                    "FK_map_observations_quality_run_decision",
                DROP CONSTRAINT IF EXISTS
                    ck_map_observations_quality_decision,
                DROP COLUMN IF EXISTS quality_decision;

            ALTER TABLE quality.map_quality_findings
                DROP CONSTRAINT IF EXISTS
                    "FK_map_quality_findings_text_snapshot",
                DROP CONSTRAINT IF EXISTS
                    "FK_map_quality_findings_item_snapshot",
                DROP CONSTRAINT IF EXISTS
                    "FK_map_quality_findings_quality_run_snapshot",
                DROP CONSTRAINT IF EXISTS
                    ck_map_quality_findings_effect;

            DROP INDEX IF EXISTS
                evidence.ux_map_text_occurrences_id_snapshot;
            DROP INDEX IF EXISTS
                evidence.ux_map_item_occurrences_id_snapshot;
            DROP INDEX IF EXISTS
                quality.ux_map_quality_runs_observation_decision;
            DROP INDEX IF EXISTS
                quality.ux_map_quality_runs_id_snapshot;

            ALTER TABLE quality.map_quality_findings
                DROP COLUMN IF EXISTS map_snapshot_id;
            """);
    }
}
