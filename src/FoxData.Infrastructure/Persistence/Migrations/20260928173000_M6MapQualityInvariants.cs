using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FoxDataDbContext))]
[Migration("20260928173000_M6MapQualityInvariants")]
public sealed class M6MapQualityInvariants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE quality.map_quality_findings
                ADD CONSTRAINT ck_map_quality_findings_effect
                CHECK (effect IN ('informational','suspect','quarantined'));

            CREATE OR REPLACE FUNCTION quality.enforce_map_quality_finding_snapshot()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                run_snapshot_id uuid;
                occurrence_snapshot_id uuid;
            BEGIN
                SELECT map_snapshot_id
                INTO run_snapshot_id
                FROM quality.map_quality_runs
                WHERE id = NEW.quality_run_id;

                IF run_snapshot_id IS NULL THEN
                    RAISE EXCEPTION
                        'quality run % does not exist', NEW.quality_run_id
                        USING ERRCODE = '23503';
                END IF;

                IF NEW.map_item_occurrence_id IS NOT NULL THEN
                    SELECT map_snapshot_id
                    INTO occurrence_snapshot_id
                    FROM evidence.map_item_occurrences
                    WHERE id = NEW.map_item_occurrence_id;

                    IF occurrence_snapshot_id IS DISTINCT FROM run_snapshot_id THEN
                        RAISE EXCEPTION
                            'map item occurrence % does not belong to quality snapshot %',
                            NEW.map_item_occurrence_id,
                            run_snapshot_id
                            USING
                                ERRCODE = '23514',
                                CONSTRAINT =
                                    'ck_map_quality_findings_snapshot_binding';
                    END IF;
                END IF;

                IF NEW.map_text_occurrence_id IS NOT NULL THEN
                    SELECT map_snapshot_id
                    INTO occurrence_snapshot_id
                    FROM evidence.map_text_occurrences
                    WHERE id = NEW.map_text_occurrence_id;

                    IF occurrence_snapshot_id IS DISTINCT FROM run_snapshot_id THEN
                        RAISE EXCEPTION
                            'map text occurrence % does not belong to quality snapshot %',
                            NEW.map_text_occurrence_id,
                            run_snapshot_id
                            USING
                                ERRCODE = '23514',
                                CONSTRAINT =
                                    'ck_map_quality_findings_snapshot_binding';
                    END IF;
                END IF;

                RETURN NEW;
            END;
            $$;

            CREATE CONSTRAINT TRIGGER ck_map_quality_findings_snapshot_binding
            AFTER INSERT OR UPDATE OF
                quality_run_id,
                map_item_occurrence_id,
                map_text_occurrence_id
            ON quality.map_quality_findings
            DEFERRABLE INITIALLY IMMEDIATE
            FOR EACH ROW
            EXECUTE FUNCTION quality.enforce_map_quality_finding_snapshot();

            CREATE OR REPLACE FUNCTION quality.enforce_map_observation_quality_decision()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                quality_decision text;
            BEGIN
                SELECT decision
                INTO quality_decision
                FROM quality.map_quality_runs
                WHERE id = NEW.quality_run_id;

                IF quality_decision IS DISTINCT FROM 'accepted' THEN
                    RAISE EXCEPTION
                        'map observation requires accepted quality run %, durable decision is %',
                        NEW.quality_run_id,
                        quality_decision
                        USING
                            ERRCODE = '23514',
                            CONSTRAINT =
                                'ck_map_observations_accepted_quality';
                END IF;

                RETURN NEW;
            END;
            $$;

            CREATE CONSTRAINT TRIGGER ck_map_observations_accepted_quality
            AFTER INSERT OR UPDATE OF quality_run_id
            ON runtime.map_observations
            DEFERRABLE INITIALLY IMMEDIATE
            FOR EACH ROW
            EXECUTE FUNCTION quality.enforce_map_observation_quality_decision();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS
                ck_map_observations_accepted_quality
                ON runtime.map_observations;
            DROP FUNCTION IF EXISTS
                quality.enforce_map_observation_quality_decision();

            DROP TRIGGER IF EXISTS
                ck_map_quality_findings_snapshot_binding
                ON quality.map_quality_findings;
            DROP FUNCTION IF EXISTS
                quality.enforce_map_quality_finding_snapshot();

            ALTER TABLE quality.map_quality_findings
                DROP CONSTRAINT IF EXISTS ck_map_quality_findings_effect;
            """);
    }
}
