using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FoxDataDbContext))]
[Migration("20260927140000_M5CoverageRecovery")]
public sealed class M5CoverageRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE evidence.coverage_observations (
                id uuid NOT NULL,
                endpoint_id uuid NOT NULL,
                collection_job_id uuid NOT NULL,
                attempt_id uuid NOT NULL,
                validation_fetch_id uuid NULL,
                representation_fetch_id uuid NULL,
                source_parse_run_id uuid NULL,
                state character varying(32) NOT NULL,
                boundary_at timestamp with time zone NOT NULL,
                detail_code character varying(128) NULL,
                recorded_at timestamp with time zone NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT "PK_coverage_observations"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_coverage_observations_endpoints_endpoint_id"
                    FOREIGN KEY (endpoint_id)
                    REFERENCES sources.endpoints (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_coverage_observations_collection_jobs_collection_job_id"
                    FOREIGN KEY (collection_job_id)
                    REFERENCES ingest.collection_jobs (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_coverage_observations_attempts_attempt_id"
                    FOREIGN KEY (attempt_id)
                    REFERENCES ingest.attempts (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_coverage_observations_fetches_validation_fetch_id"
                    FOREIGN KEY (validation_fetch_id)
                    REFERENCES evidence.fetches (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_coverage_observations_fetches_representation_fetch_id"
                    FOREIGN KEY (representation_fetch_id)
                    REFERENCES evidence.fetches (id)
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_coverage_observations_source_parse_runs_source_parse_run_id"
                    FOREIGN KEY (source_parse_run_id)
                    REFERENCES evidence.source_parse_runs (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_coverage_observations_state
                    CHECK (state IN (
                        'observed',
                        'source_not_modified',
                        'source_unavailable',
                        'collector_unavailable',
                        'rejected',
                        'unknown')),
                CONSTRAINT ck_coverage_observations_observed_lineage
                    CHECK (
                        state <> 'observed'
                        OR (
                            validation_fetch_id IS NOT NULL
                            AND representation_fetch_id = validation_fetch_id
                            AND source_parse_run_id IS NOT NULL)),
                CONSTRAINT ck_coverage_observations_not_modified_lineage
                    CHECK (
                        state <> 'source_not_modified'
                        OR (
                            validation_fetch_id IS NOT NULL
                            AND representation_fetch_id IS NOT NULL
                            AND validation_fetch_id <> representation_fetch_id
                            AND source_parse_run_id IS NOT NULL)),
                CONSTRAINT ck_coverage_observations_rejected_parse
                    CHECK (
                        state <> 'rejected'
                        OR source_parse_run_id IS NOT NULL)
            );

            CREATE UNIQUE INDEX ux_coverage_observations_attempt_id
                ON evidence.coverage_observations (attempt_id);

            CREATE INDEX ix_coverage_observations_endpoint_boundary
                ON evidence.coverage_observations
                    (endpoint_id, boundary_at, id);

            CREATE INDEX ix_coverage_observations_validation_fetch_id
                ON evidence.coverage_observations (validation_fetch_id)
                WHERE validation_fetch_id IS NOT NULL;

            CREATE INDEX ix_coverage_observations_state_boundary
                ON evidence.coverage_observations (state, boundary_at, id);

            CREATE TABLE evidence.coverage_reprocessing_runs (
                id uuid NOT NULL,
                coverage_observation_id uuid NOT NULL,
                processor_version character varying(128) NOT NULL,
                outcome character varying(32) NOT NULL,
                error_code character varying(128) NULL,
                started_at timestamp with time zone NOT NULL,
                completed_at timestamp with time zone NOT NULL,
                created_at timestamp with time zone NOT NULL
                    DEFAULT transaction_timestamp(),
                CONSTRAINT "PK_coverage_reprocessing_runs"
                    PRIMARY KEY (id),
                CONSTRAINT "FK_coverage_reprocessing_runs_coverage_observations_coverage_observation_id"
                    FOREIGN KEY (coverage_observation_id)
                    REFERENCES evidence.coverage_observations (id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_coverage_reprocessing_runs_outcome
                    CHECK (outcome IN ('applied', 'rejected')),
                CONSTRAINT ck_coverage_reprocessing_runs_error
                    CHECK (
                        (outcome = 'applied' AND error_code IS NULL)
                        OR
                        (outcome = 'rejected' AND error_code IS NOT NULL)),
                CONSTRAINT ck_coverage_reprocessing_runs_time
                    CHECK (completed_at >= started_at)
            );

            CREATE UNIQUE INDEX ux_coverage_reprocessing_runs_identity
                ON evidence.coverage_reprocessing_runs
                    (coverage_observation_id, processor_version);

            CREATE INDEX ix_coverage_reprocessing_runs_processor_outcome
                ON evidence.coverage_reprocessing_runs
                    (processor_version, outcome, created_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS evidence.coverage_reprocessing_runs;
            DROP TABLE IF EXISTS evidence.coverage_observations;
            """);
    }
}
