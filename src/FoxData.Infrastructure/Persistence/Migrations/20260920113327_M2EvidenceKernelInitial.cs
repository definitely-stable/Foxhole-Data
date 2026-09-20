using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2EvidenceKernelInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ingest");

            migrationBuilder.EnsureSchema(
                name: "sources");

            migrationBuilder.EnsureSchema(
                name: "evidence");

            migrationBuilder.CreateTable(
                name: "payloads",
                schema: "evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    byte_length = table.Column<long>(type: "bigint", nullable: false),
                    body = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payloads", x => x.id);
                    table.CheckConstraint("ck_payloads_body_length", "octet_length(body) = byte_length");
                    table.CheckConstraint("ck_payloads_byte_length", "byte_length >= 0");
                    table.CheckConstraint("ck_payloads_sha256_length", "octet_length(sha256) = 32");
                });

            migrationBuilder.CreateTable(
                name: "sources",
                schema: "sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shards",
                schema: "sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    environment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shards", x => x.id);
                    table.ForeignKey(
                        name: "FK_shards_sources_source_id",
                        column: x => x.source_id,
                        principalSchema: "sources",
                        principalTable: "sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "endpoints",
                schema: "sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shard_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    semantic_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoints", x => x.id);
                    table.ForeignKey(
                        name: "FK_endpoints_shards_shard_id",
                        column: x => x.shard_id,
                        principalSchema: "sources",
                        principalTable: "shards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collection_jobs",
                schema: "ingest",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lease_owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_generation = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_jobs", x => x.id);
                    table.CheckConstraint("ck_collection_jobs_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_collection_jobs_lease_generation", "lease_generation >= 0");
                    table.CheckConstraint("ck_collection_jobs_state", "state IN ('pending','leased','processing','completed','failed','cancelled')");
                    table.ForeignKey(
                        name: "FK_collection_jobs_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "sources",
                        principalTable: "endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attempts",
                schema: "ingest",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    lease_generation = table.Column<long>(type: "bigint", nullable: false),
                    fence_token = table.Column<long>(type: "bigint", nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outcome_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exchange_authorized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raw_durable_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recovered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_class = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attempts", x => x.id);
                    table.CheckConstraint("ck_attempts_attempt_number", "attempt_number > 0");
                    table.CheckConstraint("ck_attempts_fence_token", "fence_token IS NULL OR fence_token >= 0");
                    table.CheckConstraint("ck_attempts_lease_generation", "lease_generation > 0");
                    table.CheckConstraint("ck_attempts_state", "state IN ('created','fenced','exchange_authorized','raw_durable','completed','failed','uncertain','superseded','captured_late')");
                    table.ForeignKey(
                        name: "FK_attempts_collection_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "ingest",
                        principalTable: "collection_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "endpoint_state",
                schema: "ingest",
                columns: table => new
                {
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fence_token = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    active_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_authoritative_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint_state", x => x.endpoint_id);
                    table.CheckConstraint("ck_endpoint_state_fence_token", "fence_token >= 0");
                    table.ForeignKey(
                        name: "FK_endpoint_state_attempts_active_attempt_id",
                        column: x => x.active_attempt_id,
                        principalSchema: "ingest",
                        principalTable: "attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_endpoint_state_attempts_last_authoritative_attempt_id",
                        column: x => x.last_authoritative_attempt_id,
                        principalSchema: "ingest",
                        principalTable: "attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_endpoint_state_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "sources",
                        principalTable: "endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fetches",
                schema: "evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    response_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transport_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    media_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    content_encoding = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    declared_length = table.Column<long>(type: "bigint", nullable: true),
                    source_etag = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    cache_control = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payload_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prior_fetch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "transaction_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fetches", x => x.id);
                    table.CheckConstraint("ck_fetches_declared_length", "declared_length IS NULL OR declared_length >= 0");
                    table.CheckConstraint("ck_fetches_duration_ms", "duration_ms >= 0");
                    table.ForeignKey(
                        name: "FK_fetches_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalSchema: "ingest",
                        principalTable: "attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fetches_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalSchema: "sources",
                        principalTable: "endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fetches_fetches_prior_fetch_id",
                        column: x => x.prior_fetch_id,
                        principalSchema: "evidence",
                        principalTable: "fetches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fetches_payloads_payload_id",
                        column: x => x.payload_id,
                        principalSchema: "evidence",
                        principalTable: "payloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attempts_state_started",
                schema: "ingest",
                table: "attempts",
                columns: new[] { "state", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ux_attempts_job_number",
                schema: "ingest",
                table: "attempts",
                columns: new[] { "job_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collection_jobs_endpoint_scheduled",
                schema: "ingest",
                table: "collection_jobs",
                columns: new[] { "endpoint_id", "scheduled_for" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_jobs_expired_lease",
                schema: "ingest",
                table: "collection_jobs",
                columns: new[] { "lease_expires_at", "id" },
                filter: "state IN ('leased','processing')");

            migrationBuilder.CreateIndex(
                name: "ix_collection_jobs_pending",
                schema: "ingest",
                table: "collection_jobs",
                columns: new[] { "available_at", "priority", "id" },
                descending: new[] { false, true, false },
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ux_collection_jobs_endpoint_idempotency",
                schema: "ingest",
                table: "collection_jobs",
                columns: new[] { "endpoint_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_state_active_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                column: "active_attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_state_last_authoritative_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                column: "last_authoritative_attempt_id");

            migrationBuilder.CreateIndex(
                name: "ux_endpoints_shard_semantic_key",
                schema: "sources",
                table: "endpoints",
                columns: new[] { "shard_id", "semantic_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fetches_endpoint_retrieved",
                schema: "evidence",
                table: "fetches",
                columns: new[] { "endpoint_id", "retrieved_at", "id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "ix_fetches_payload",
                schema: "evidence",
                table: "fetches",
                column: "payload_id");

            migrationBuilder.CreateIndex(
                name: "ix_fetches_prior_fetch",
                schema: "evidence",
                table: "fetches",
                column: "prior_fetch_id");

            migrationBuilder.CreateIndex(
                name: "ux_fetches_attempt",
                schema: "evidence",
                table: "fetches",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_payloads_sha256",
                schema: "evidence",
                table: "payloads",
                column: "sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_shards_source_key",
                schema: "sources",
                table: "shards",
                columns: new[] { "source_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sources_key",
                schema: "sources",
                table: "sources",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "endpoint_state",
                schema: "ingest");

            migrationBuilder.DropTable(
                name: "fetches",
                schema: "evidence");

            migrationBuilder.DropTable(
                name: "attempts",
                schema: "ingest");

            migrationBuilder.DropTable(
                name: "payloads",
                schema: "evidence");

            migrationBuilder.DropTable(
                name: "collection_jobs",
                schema: "ingest");

            migrationBuilder.DropTable(
                name: "endpoints",
                schema: "sources");

            migrationBuilder.DropTable(
                name: "shards",
                schema: "sources");

            migrationBuilder.DropTable(
                name: "sources",
                schema: "sources");
        }
    }
}
