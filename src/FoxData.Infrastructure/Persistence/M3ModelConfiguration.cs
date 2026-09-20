using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoxData.Infrastructure.Persistence;

internal static class M3ModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureEndpointPollState(modelBuilder.Entity<EndpointPollStateRow>());
        ConfigureSourceParseRun(modelBuilder.Entity<SourceParseRunRow>());
    }

    private static void ConfigureEndpointPollState(
        EntityTypeBuilder<EndpointPollStateRow> builder)
    {
        builder.ToTable(
            "endpoint_poll_state",
            "ingest",
            table => table.HasCheckConstraint(
                "ck_endpoint_poll_state_consecutive_failures",
                "consecutive_failures >= 0"));

        builder.HasKey(x => x.EndpointId);

        builder.Property(x => x.EndpointId)
            .HasColumnName("endpoint_id")
            .ValueGeneratedNever();
        builder.Property(x => x.LastProcessedFetchId)
            .HasColumnName("last_processed_fetch_id");
        builder.Property(x => x.LatestValidationFetchId)
            .HasColumnName("latest_validation_fetch_id");
        builder.Property(x => x.RepresentationFetchId)
            .HasColumnName("representation_fetch_id");
        builder.Property(x => x.ValidatorEtag)
            .HasColumnName("validator_etag")
            .HasMaxLength(1024);
        builder.Property(x => x.SourceCacheEligibleAt)
            .HasColumnName("source_cache_eligible_at");
        builder.Property(x => x.NextTargetAt)
            .HasColumnName("next_target_at");
        builder.Property(x => x.RetryEligibleAt)
            .HasColumnName("retry_eligible_at");
        builder.Property(x => x.LastHttpResponseAt)
            .HasColumnName("last_http_response_at");
        builder.Property(x => x.LastSuccessAt)
            .HasColumnName("last_success_at");
        builder.Property(x => x.ConsecutiveFailures)
            .HasColumnName("consecutive_failures")
            .HasDefaultValue(0);
        builder.Property(x => x.PolicyVersion)
            .HasColumnName("policy_version")
            .HasMaxLength(128);
        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasOne<EndpointRow>()
            .WithOne()
            .HasForeignKey<EndpointPollStateRow>(x => x.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FetchRow>()
            .WithMany()
            .HasForeignKey(x => x.LastProcessedFetchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FetchRow>()
            .WithMany()
            .HasForeignKey(x => x.LatestValidationFetchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FetchRow>()
            .WithMany()
            .HasForeignKey(x => x.RepresentationFetchId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSourceParseRun(
        EntityTypeBuilder<SourceParseRunRow> builder)
    {
        builder.ToTable(
            "source_parse_runs",
            "evidence",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_source_parse_runs_unknown_property_count",
                    "unknown_property_count >= 0");
                table.HasCheckConstraint(
                    "ck_source_parse_runs_unknown_code_count",
                    "unknown_code_count >= 0");
                table.HasCheckConstraint(
                    "ck_source_parse_runs_completed_after_started",
                    "completed_at >= started_at");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.Property(x => x.RepresentationFetchId)
            .HasColumnName("representation_fetch_id");
        builder.Property(x => x.CapabilityKey)
            .HasColumnName("capability_key")
            .HasMaxLength(128);
        builder.Property(x => x.AdapterVersion)
            .HasColumnName("adapter_version")
            .HasMaxLength(128);
        builder.Property(x => x.ParserVersion)
            .HasColumnName("parser_version")
            .HasMaxLength(128);
        builder.Property(x => x.FingerprintAlgorithm)
            .HasColumnName("fingerprint_algorithm")
            .HasMaxLength(64);
        builder.Property(x => x.StructuralFingerprint)
            .HasColumnName("structural_fingerprint")
            .HasMaxLength(128);
        builder.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasMaxLength(64);
        builder.Property(x => x.UnknownPropertyCount)
            .HasColumnName("unknown_property_count");
        builder.Property(x => x.UnknownCodeCount)
            .HasColumnName("unknown_code_count");
        builder.Property(x => x.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(128);
        builder.Property(x => x.StartedAt)
            .HasColumnName("started_at");
        builder.Property(x => x.CompletedAt)
            .HasColumnName("completed_at");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();
        builder.Property(x => x.SourceVersion)
            .HasColumnName("source_version");
        builder.Property(x => x.SourceLastUpdated)
            .HasColumnName("source_last_updated");
        builder.Property(x => x.DecodedByteLength)
            .HasColumnName("decoded_byte_length");

        builder.HasIndex(
                x => new
                {
                    x.RepresentationFetchId,
                    x.CapabilityKey,
                    x.ParserVersion,
                })
            .IsUnique()
            .HasDatabaseName("ux_source_parse_runs_representation_capability_parser");

        builder.HasIndex(x => new { x.Outcome, x.CreatedAt })
            .HasDatabaseName("ix_source_parse_runs_outcome_created");

        builder.HasOne<FetchRow>()
            .WithMany()
            .HasForeignKey(x => x.RepresentationFetchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
