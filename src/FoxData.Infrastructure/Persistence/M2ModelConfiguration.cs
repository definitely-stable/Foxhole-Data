using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoxData.Infrastructure.Persistence;

internal static class M2ModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureSource(modelBuilder.Entity<SourceRow>());
        ConfigureShard(modelBuilder.Entity<ShardRow>());
        ConfigureEndpoint(modelBuilder.Entity<EndpointRow>());
        ConfigureCollectionJob(modelBuilder.Entity<CollectionJobRow>());
        ConfigureAttempt(modelBuilder.Entity<IngestionAttemptRow>());
        ConfigureEndpointState(modelBuilder.Entity<EndpointStateRow>());
        ConfigurePayload(modelBuilder.Entity<PayloadRow>());
        ConfigureFetch(modelBuilder.Entity<FetchRow>());
    }

    private static void ConfigureSource(EntityTypeBuilder<SourceRow> builder)
    {
        builder.ToTable("sources", "sources");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(128);
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
        builder.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        ConfigureCreatedUpdated(builder);

        builder.HasIndex(x => x.Key).IsUnique().HasDatabaseName("ux_sources_key");
    }

    private static void ConfigureShard(EntityTypeBuilder<ShardRow> builder)
    {
        builder.ToTable("shards", "sources");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.SourceId).HasColumnName("source_id");
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(128);
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
        builder.Property(x => x.Environment).HasColumnName("environment").HasMaxLength(64);
        builder.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        ConfigureCreatedUpdated(builder);

        builder.HasIndex(x => new { x.SourceId, x.Key })
            .IsUnique()
            .HasDatabaseName("ux_shards_source_key");

        builder.HasOne<SourceRow>()
            .WithMany()
            .HasForeignKey(x => x.SourceId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureEndpoint(EntityTypeBuilder<EndpointRow> builder)
    {
        builder.ToTable("endpoints", "sources");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ShardId).HasColumnName("shard_id");
        builder.Property(x => x.CapabilityKey).HasColumnName("capability_key").HasMaxLength(128);
        builder.Property(x => x.SemanticKey).HasColumnName("semantic_key").HasMaxLength(256);
        builder.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        ConfigureCreatedUpdated(builder);

        builder.HasIndex(x => new { x.ShardId, x.SemanticKey })
            .IsUnique()
            .HasDatabaseName("ux_endpoints_shard_semantic_key");

        builder.HasOne<ShardRow>()
            .WithMany()
            .HasForeignKey(x => x.ShardId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCollectionJob(EntityTypeBuilder<CollectionJobRow> builder)
    {
        builder.ToTable(
            "collection_jobs",
            "ingest",
            table =>
            {
                table.HasCheckConstraint("ck_collection_jobs_lease_generation", "lease_generation >= 0");
                table.HasCheckConstraint("ck_collection_jobs_attempt_count", "attempt_count >= 0");
                table.HasCheckConstraint(
                    "ck_collection_jobs_state",
                    "state IN ('pending','leased','processing','completed','failed','cancelled')");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.EndpointId).HasColumnName("endpoint_id");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(256);
        builder.Property(x => x.ScheduledFor).HasColumnName("scheduled_for");
        builder.Property(x => x.AvailableAt).HasColumnName("available_at");
        builder.Property(x => x.Priority).HasColumnName("priority").HasDefaultValue((short)0);
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
        builder.Property(x => x.LeaseOwnerId).HasColumnName("lease_owner_id");
        builder.Property(x => x.LeaseGeneration).HasColumnName("lease_generation").HasDefaultValue(0L);
        builder.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        ConfigureCreatedUpdated(builder);

        builder.HasIndex(x => new { x.EndpointId, x.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_collection_jobs_endpoint_idempotency");

        builder.HasIndex(x => new { x.AvailableAt, x.Priority, x.Id })
            .HasDatabaseName("ix_collection_jobs_pending")
            .HasFilter("state = 'pending'")
            .IsDescending(false, true, false);

        builder.HasIndex(x => new { x.LeaseExpiresAt, x.Id })
            .HasDatabaseName("ix_collection_jobs_expired_lease")
            .HasFilter("state IN ('leased','processing')");

        builder.HasIndex(x => new { x.EndpointId, x.ScheduledFor })
            .HasDatabaseName("ix_collection_jobs_endpoint_scheduled");

        builder.HasOne<EndpointRow>()
            .WithMany()
            .HasForeignKey(x => x.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAttempt(EntityTypeBuilder<IngestionAttemptRow> builder)
    {
        builder.ToTable(
            "attempts",
            "ingest",
            table =>
            {
                table.HasCheckConstraint("ck_attempts_attempt_number", "attempt_number > 0");
                table.HasCheckConstraint("ck_attempts_lease_generation", "lease_generation > 0");
                table.HasCheckConstraint("ck_attempts_fence_token", "fence_token IS NULL OR fence_token >= 0");
                table.HasCheckConstraint(
                    "ck_attempts_state",
                    "state IN ('created','fenced','exchange_authorized','raw_durable','completed','failed','uncertain','superseded','captured_late')");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
        builder.Property(x => x.LeaseGeneration).HasColumnName("lease_generation");
        builder.Property(x => x.FenceToken).HasColumnName("fence_token");
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
        builder.Property(x => x.OutcomeCode).HasColumnName("outcome_code").HasMaxLength(128);
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.ExchangeAuthorizedAt).HasColumnName("exchange_authorized_at");
        builder.Property(x => x.RawDurableAt).HasColumnName("raw_durable_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.RecoveredAt).HasColumnName("recovered_at");
        builder.Property(x => x.SupersededAt).HasColumnName("superseded_at");
        builder.Property(x => x.ErrorClass).HasColumnName("error_class").HasMaxLength(256);
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(128);
        ConfigureCreatedUpdated(builder);

        builder.HasIndex(x => new { x.JobId, x.AttemptNumber })
            .IsUnique()
            .HasDatabaseName("ux_attempts_job_number");

        builder.HasIndex(x => new { x.State, x.StartedAt })
            .HasDatabaseName("ix_attempts_state_started");

        builder.HasOne<CollectionJobRow>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureEndpointState(EntityTypeBuilder<EndpointStateRow> builder)
    {
        builder.ToTable(
            "endpoint_state",
            "ingest",
            table => table.HasCheckConstraint("ck_endpoint_state_fence_token", "fence_token >= 0"));

        builder.HasKey(x => x.EndpointId);

        builder.Property(x => x.EndpointId).HasColumnName("endpoint_id").ValueGeneratedNever();
        builder.Property(x => x.FenceToken).HasColumnName("fence_token").HasDefaultValue(0L);
        builder.Property(x => x.ActiveAttemptId).HasColumnName("active_attempt_id");
        builder.Property(x => x.LastCurrentCaptureAttemptId).HasColumnName("last_current_capture_attempt_id");
        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasOne<EndpointRow>()
            .WithOne()
            .HasForeignKey<EndpointStateRow>(x => x.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<IngestionAttemptRow>()
            .WithMany()
            .HasForeignKey(x => x.ActiveAttemptId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<IngestionAttemptRow>()
            .WithMany()
            .HasForeignKey(x => x.LastCurrentCaptureAttemptId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePayload(EntityTypeBuilder<PayloadRow> builder)
    {
        builder.ToTable(
            "payloads",
            "evidence",
            table =>
            {
                table.HasCheckConstraint("ck_payloads_sha256_length", "octet_length(sha256) = 32");
                table.HasCheckConstraint("ck_payloads_byte_length", "byte_length >= 0");
                table.HasCheckConstraint("ck_payloads_body_length", "octet_length(body) = byte_length");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Sha256).HasColumnName("sha256");
        builder.Property(x => x.ByteLength).HasColumnName("byte_length");
        builder.Property(x => x.Body).HasColumnName("body");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.Sha256)
            .IsUnique()
            .HasDatabaseName("ux_payloads_sha256");
    }

    private static void ConfigureFetch(EntityTypeBuilder<FetchRow> builder)
    {
        builder.ToTable(
            "fetches",
            "evidence",
            table =>
            {
                table.HasCheckConstraint("ck_fetches_duration_ms", "duration_ms >= 0");
                table.HasCheckConstraint(
                    "ck_fetches_declared_length",
                    "declared_length IS NULL OR declared_length >= 0");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.AttemptId).HasColumnName("attempt_id");
        builder.Property(x => x.EndpointId).HasColumnName("endpoint_id");
        builder.Property(x => x.RequestStartedAt).HasColumnName("request_started_at");
        builder.Property(x => x.ResponseStartedAt).HasColumnName("response_started_at");
        builder.Property(x => x.RetrievedAt).HasColumnName("retrieved_at");
        builder.Property(x => x.TransportKind).HasColumnName("transport_kind").HasMaxLength(64);
        builder.Property(x => x.StatusCode).HasColumnName("status_code");
        builder.Property(x => x.MediaType).HasColumnName("media_type").HasMaxLength(256);
        builder.Property(x => x.ContentEncoding).HasColumnName("content_encoding").HasMaxLength(128);
        builder.Property(x => x.DeclaredLength).HasColumnName("declared_length");
        builder.Property(x => x.SourceEtag).HasColumnName("source_etag").HasMaxLength(1024);
        builder.Property(x => x.CacheControl).HasColumnName("cache_control").HasMaxLength(2048);
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.PayloadId).HasColumnName("payload_id");
        builder.Property(x => x.PriorFetchId).HasColumnName("prior_fetch_id");
        builder.Property(x => x.DurationMs).HasColumnName("duration_ms");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.AttemptId)
            .IsUnique()
            .HasDatabaseName("ux_fetches_attempt");

        builder.HasIndex(x => new { x.EndpointId, x.RetrievedAt, x.Id })
            .HasDatabaseName("ix_fetches_endpoint_retrieved")
            .IsDescending(false, true, false);

        builder.HasIndex(x => x.PayloadId)
            .HasDatabaseName("ix_fetches_payload");

        builder.HasIndex(x => x.PriorFetchId)
            .HasDatabaseName("ix_fetches_prior_fetch");

        builder.HasOne<IngestionAttemptRow>()
            .WithMany()
            .HasForeignKey(x => x.AttemptId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<EndpointRow>()
            .WithMany()
            .HasForeignKey(x => x.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PayloadRow>()
            .WithMany()
            .HasForeignKey(x => x.PayloadId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FetchRow>()
            .WithMany()
            .HasForeignKey(x => x.PriorFetchId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCreatedUpdated<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<DateTimeOffset>("CreatedAt")
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.Property<DateTimeOffset>("UpdatedAt")
            .HasColumnName("updated_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();
    }
}
