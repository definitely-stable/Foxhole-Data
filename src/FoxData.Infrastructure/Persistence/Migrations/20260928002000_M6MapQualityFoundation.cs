using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FoxDataDbContext))]
[Migration("20260928002000_M6MapQualityFoundation")]
public sealed class M6MapQualityFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE SCHEMA IF NOT EXISTS quality;

            CREATE TABLE evidence.map_snapshots (
                id uuid NOT NULL,
                normalization_run_id uuid NOT NULL,
                source_parse_run_id uuid NOT NULL,
                representation_fetch_id uuid NOT NULL,
                capability_kind character varying(16) NOT NULL,
                source_map_name character varying(256) NOT NULL,
                source_region_id integer NULL,
                source_scorched_victory_towns integer NULL,
                source_version bigint NULL,
                source_last_updated_ms bigint NULL,
                source_updated_at timestamp with time zone NULL,
                item_count integer NOT NULL,
                text_item_count integer NOT NULL,
                recorded_at timestamp with time zone NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT "PK_map_snapshots"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_map_snapshots_normalization_runs_normalization_run_id"
                    FOREIGN KEY (normalization_run_id)
                    REFERENCES evidence.normalization_runs (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_snapshots_source_parse_runs_source_parse_run_id"
                    FOREIGN KEY (source_parse_run_id)
                    REFERENCES evidence.source_parse_runs (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_snapshots_fetches_representation_fetch_id"
                    FOREIGN KEY (representation_fetch_id)
                    REFERENCES evidence.fetches (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_map_snapshots_capability_kind
                    CHECK (capability_kind IN ('static','dynamic')),
                CONSTRAINT ck_map_snapshots_item_count
                    CHECK (item_count >= 0),
                CONSTRAINT ck_map_snapshots_text_item_count
                    CHECK (text_item_count >= 0)
            );

            CREATE UNIQUE INDEX ux_map_snapshots_normalization_run
                ON evidence.map_snapshots (normalization_run_id);

            CREATE INDEX ix_map_snapshots_source_parse
                ON evidence.map_snapshots (source_parse_run_id);

            CREATE INDEX ix_map_snapshots_representation_fetch
                ON evidence.map_snapshots (representation_fetch_id);

            CREATE INDEX ix_map_snapshots_source_map_kind
                ON evidence.map_snapshots
                    (source_map_name, capability_kind, recorded_at);

            CREATE UNIQUE INDEX ux_map_snapshots_id_kind
                ON evidence.map_snapshots (id, capability_kind);

            CREATE TABLE evidence.map_item_occurrences (
                id uuid NOT NULL,
                map_snapshot_id uuid NOT NULL,
                source_ordinal integer NOT NULL,
                raw_team_id text NULL,
                raw_icon_type integer NULL,
                x double precision NULL,
                y double precision NULL,
                raw_flags integer NULL,
                raw_view_direction integer NULL,
                CONSTRAINT "PK_map_item_occurrences"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_map_item_occurrences_map_snapshots_map_snapshot_id"
                    FOREIGN KEY (map_snapshot_id)
                    REFERENCES evidence.map_snapshots (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_map_item_occurrences_source_ordinal
                    CHECK (source_ordinal >= 0)
            );

            CREATE UNIQUE INDEX ux_map_item_occurrences_snapshot_ordinal
                ON evidence.map_item_occurrences
                    (map_snapshot_id, source_ordinal);

            CREATE TABLE evidence.map_text_occurrences (
                id uuid NOT NULL,
                map_snapshot_id uuid NOT NULL,
                source_ordinal integer NOT NULL,
                text text NULL,
                x double precision NULL,
                y double precision NULL,
                raw_map_marker_type text NULL,
                CONSTRAINT "PK_map_text_occurrences"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_map_text_occurrences_map_snapshots_map_snapshot_id"
                    FOREIGN KEY (map_snapshot_id)
                    REFERENCES evidence.map_snapshots (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_map_text_occurrences_source_ordinal
                    CHECK (source_ordinal >= 0)
            );

            CREATE UNIQUE INDEX ux_map_text_occurrences_snapshot_ordinal
                ON evidence.map_text_occurrences
                    (map_snapshot_id, source_ordinal);

            CREATE TABLE quality.map_quality_runs (
                id uuid NOT NULL,
                map_snapshot_id uuid NOT NULL,
                war_region_id uuid NOT NULL,
                validation_fetch_id uuid NOT NULL,
                taxonomy_version character varying(128) NOT NULL,
                quality_policy_version character varying(128) NOT NULL,
                baseline_map_observation_id uuid NULL,
                decision character varying(32) NOT NULL,
                started_at timestamp with time zone NOT NULL,
                completed_at timestamp with time zone NOT NULL,
                created_at timestamp with time zone NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT "PK_map_quality_runs"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_map_quality_runs_map_snapshots_map_snapshot_id"
                    FOREIGN KEY (map_snapshot_id)
                    REFERENCES evidence.map_snapshots (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_quality_runs_war_regions_war_region_id"
                    FOREIGN KEY (war_region_id)
                    REFERENCES runtime.war_regions (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_quality_runs_fetches_validation_fetch_id"
                    FOREIGN KEY (validation_fetch_id)
                    REFERENCES evidence.fetches (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_map_quality_runs_decision
                    CHECK (decision IN ('accepted','suspect','quarantined')),
                CONSTRAINT ck_map_quality_runs_time
                    CHECK (completed_at >= started_at)
            );

            CREATE UNIQUE INDEX ux_map_quality_runs_identity
                ON quality.map_quality_runs
                    (map_snapshot_id, war_region_id, validation_fetch_id,
                     taxonomy_version, quality_policy_version);

            CREATE UNIQUE INDEX ux_map_quality_runs_binding
                ON quality.map_quality_runs
                    (id, map_snapshot_id, war_region_id, validation_fetch_id);

            CREATE INDEX ix_map_quality_runs_region_decision
                ON quality.map_quality_runs
                    (war_region_id, decision, created_at);

            CREATE TABLE quality.map_quality_findings (
                id uuid NOT NULL,
                quality_run_id uuid NOT NULL,
                rule_key character varying(128) NOT NULL,
                rule_version character varying(128) NOT NULL,
                configuration_version character varying(128) NOT NULL,
                effect character varying(32) NOT NULL,
                map_item_occurrence_id uuid NULL,
                map_text_occurrence_id uuid NULL,
                detail_code character varying(128) NULL,
                input_metrics jsonb NOT NULL,
                created_at timestamp with time zone NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT "PK_map_quality_findings"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_map_quality_findings_map_quality_runs_quality_run_id"
                    FOREIGN KEY (quality_run_id)
                    REFERENCES quality.map_quality_runs (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_quality_findings_map_item_occurrences_map_item_occurrence_id"
                    FOREIGN KEY (map_item_occurrence_id)
                    REFERENCES evidence.map_item_occurrences (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_quality_findings_map_text_occurrences_map_text_occurrence_id"
                    FOREIGN KEY (map_text_occurrence_id)
                    REFERENCES evidence.map_text_occurrences (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_map_quality_findings_occurrence_reference
                    CHECK (
                        map_item_occurrence_id IS NULL
                        OR map_text_occurrence_id IS NULL),
                CONSTRAINT ck_map_quality_findings_input_metrics
                    CHECK (jsonb_typeof(input_metrics) = 'object')
            );

            CREATE INDEX ix_map_quality_findings_run_rule
                ON quality.map_quality_findings
                    (quality_run_id, rule_key);

            CREATE TABLE runtime.map_observations (
                id uuid NOT NULL,
                war_region_id uuid NOT NULL,
                map_snapshot_id uuid NOT NULL,
                quality_run_id uuid NOT NULL,
                validation_fetch_id uuid NOT NULL,
                capability_kind character varying(16) NOT NULL,
                observed_at timestamp with time zone NOT NULL,
                source_updated_at timestamp with time zone NULL,
                recorded_at timestamp with time zone NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT "PK_map_observations"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_map_observations_war_regions_war_region_id"
                    FOREIGN KEY (war_region_id)
                    REFERENCES runtime.war_regions (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_observations_map_snapshots_map_snapshot_id_capability_kind"
                    FOREIGN KEY (map_snapshot_id, capability_kind)
                    REFERENCES evidence.map_snapshots (id, capability_kind)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_observations_map_quality_runs_binding"
                    FOREIGN KEY (
                        quality_run_id,
                        map_snapshot_id,
                        war_region_id,
                        validation_fetch_id)
                    REFERENCES quality.map_quality_runs (
                        id,
                        map_snapshot_id,
                        war_region_id,
                        validation_fetch_id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_map_observations_fetches_validation_fetch_id"
                    FOREIGN KEY (validation_fetch_id)
                    REFERENCES evidence.fetches (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_map_observations_capability_kind
                    CHECK (capability_kind IN ('static','dynamic'))
            );

            CREATE UNIQUE INDEX ux_map_observations_quality_run
                ON runtime.map_observations (quality_run_id);

            CREATE INDEX ix_map_observations_region_kind_observed
                ON runtime.map_observations
                    (war_region_id, capability_kind, observed_at, id);

            CREATE INDEX ix_map_observations_snapshot
                ON runtime.map_observations (map_snapshot_id);

            ALTER TABLE quality.map_quality_runs
                ADD CONSTRAINT "FK_map_quality_runs_map_observations_baseline"
                FOREIGN KEY (baseline_map_observation_id)
                REFERENCES runtime.map_observations (id)
                ON DELETE RESTRICT;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE quality.map_quality_runs
                DROP CONSTRAINT IF EXISTS
                    "FK_map_quality_runs_map_observations_baseline";

            DROP TABLE IF EXISTS runtime.map_observations;
            DROP TABLE IF EXISTS quality.map_quality_findings;
            DROP TABLE IF EXISTS quality.map_quality_runs;
            DROP TABLE IF EXISTS evidence.map_text_occurrences;
            DROP TABLE IF EXISTS evidence.map_item_occurrences;
            DROP TABLE IF EXISTS evidence.map_snapshots;
            """);
    }
}
