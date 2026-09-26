using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoxData.Infrastructure.Persistence;

internal static class M5ModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureNormalizationRun(modelBuilder.Entity<NormalizationRunRow>());
        ConfigureWar(modelBuilder.Entity<WarRow>());
        ConfigureRegion(modelBuilder.Entity<RegionRow>());
        ConfigureWarRegion(modelBuilder.Entity<WarRegionRow>());
        ConfigureWarObservation(modelBuilder.Entity<WarObservationRow>());
        ConfigureWarReportObservation(modelBuilder.Entity<WarReportObservationRow>());
    }

    private static void ConfigureNormalizationRun(EntityTypeBuilder<NormalizationRunRow> builder)
    {
        builder.ToTable(
            "normalization_runs",
            "evidence",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_normalization_runs_completed_after_started",
                    "completed_at >= started_at");
                table.HasCheckConstraint(
                    "ck_normalization_runs_outcome",
                    "outcome IN ('normalized','rejected','failed')");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.SourceParseRunId).HasColumnName("source_parse_run_id");
        builder.Property(x => x.NormalizerVersion)
            .HasColumnName("normalizer_version")
            .HasMaxLength(128);
        builder.Property(x => x.Outcome)
            .HasColumnName("outcome")
            .HasMaxLength(32);
        builder.Property(x => x.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(128);
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(x => new { x.SourceParseRunId, x.NormalizerVersion })
            .IsUnique()
            .HasDatabaseName("ux_normalization_runs_parse_normalizer");

        builder.HasIndex(x => new { x.Outcome, x.CreatedAt })
            .HasDatabaseName("ix_normalization_runs_outcome_created");

        builder.HasOne<SourceParseRunRow>()
            .WithMany()
            .HasForeignKey(x => x.SourceParseRunId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWar(EntityTypeBuilder<WarRow> builder)
    {
        builder.ToTable(
            "wars",
            "runtime",
            table => table.HasCheckConstraint(
                "ck_wars_observation_window",
                "last_observed_at >= first_observed_at"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.ShardId).HasColumnName("shard_id");
        builder.Property(x => x.SourceWarId)
            .HasColumnName("source_war_id")
            .HasMaxLength(256);
        builder.Property(x => x.WarNumber).HasColumnName("war_number");
        builder.Property(x => x.FirstObservedAt).HasColumnName("first_observed_at");
        builder.Property(x => x.LastObservedAt).HasColumnName("last_observed_at");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(x => new { x.ShardId, x.SourceWarId })
            .IsUnique()
            .HasDatabaseName("ux_wars_shard_source_war");

        builder.HasIndex(x => new { x.ShardId, x.WarNumber })
            .HasDatabaseName("ix_wars_shard_war_number");

        builder.HasOne<ShardRow>()
            .WithMany()
            .HasForeignKey(x => x.ShardId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRegion(EntityTypeBuilder<RegionRow> builder)
    {
        builder.ToTable("regions", "runtime");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.CanonicalKey)
            .HasColumnName("canonical_key")
            .HasMaxLength(256);
        builder.Property(x => x.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(256);
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.CanonicalKey)
            .IsUnique()
            .HasDatabaseName("ux_regions_canonical_key");
    }

    private static void ConfigureWarRegion(EntityTypeBuilder<WarRegionRow> builder)
    {
        builder.ToTable(
            "war_regions",
            "runtime",
            table => table.HasCheckConstraint(
                "ck_war_regions_observation_window",
                "last_seen_at >= first_seen_at"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.WarId).HasColumnName("war_id");
        builder.Property(x => x.RegionId).HasColumnName("region_id");
        builder.Property(x => x.SourceMapName)
            .HasColumnName("source_map_name")
            .HasMaxLength(256);
        builder.Property(x => x.SourceRegionId).HasColumnName("source_region_id");
        builder.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(x => new { x.WarId, x.SourceMapName })
            .IsUnique()
            .HasDatabaseName("ux_war_regions_war_source_map");

        builder.HasIndex(x => new { x.WarId, x.RegionId })
            .HasDatabaseName("ix_war_regions_war_region");

        builder.HasOne<WarRow>()
            .WithMany()
            .HasForeignKey(x => x.WarId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<RegionRow>()
            .WithMany()
            .HasForeignKey(x => x.RegionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWarObservation(EntityTypeBuilder<WarObservationRow> builder)
    {
        builder.ToTable(
            "war_observations",
            "runtime",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_war_observations_required_victory_towns",
                    "required_victory_towns IS NULL OR required_victory_towns >= 0");
                table.HasCheckConstraint(
                    "ck_war_observations_short_required_victory_towns",
                    "short_required_victory_towns IS NULL OR short_required_victory_towns >= 0");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.WarId).HasColumnName("war_id");
        builder.Property(x => x.NormalizationRunId).HasColumnName("normalization_run_id");
        builder.Property(x => x.RepresentationFetchId).HasColumnName("representation_fetch_id");
        builder.Property(x => x.ObservedAt).HasColumnName("observed_at");
        builder.Property(x => x.RecordedAt)
            .HasColumnName("recorded_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();
        builder.Property(x => x.WarNumber).HasColumnName("war_number");
        builder.Property(x => x.Winner)
            .HasColumnName("winner")
            .HasMaxLength(128);
        builder.Property(x => x.ConquestStartTime).HasColumnName("conquest_start_time");
        builder.Property(x => x.ConquestEndTime).HasColumnName("conquest_end_time");
        builder.Property(x => x.ResistanceStartTime).HasColumnName("resistance_start_time");
        builder.Property(x => x.ScheduledConquestEndTime).HasColumnName("scheduled_conquest_end_time");
        builder.Property(x => x.RequiredVictoryTowns).HasColumnName("required_victory_towns");
        builder.Property(x => x.ShortRequiredVictoryTowns).HasColumnName("short_required_victory_towns");

        builder.HasIndex(x => x.NormalizationRunId)
            .IsUnique()
            .HasDatabaseName("ux_war_observations_normalization_run");

        builder.HasIndex(x => new { x.WarId, x.ObservedAt, x.Id })
            .HasDatabaseName("ix_war_observations_war_observed");

        builder.HasOne<WarRow>()
            .WithMany()
            .HasForeignKey(x => x.WarId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<NormalizationRunRow>()
            .WithMany()
            .HasForeignKey(x => x.NormalizationRunId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FetchRow>()
            .WithMany()
            .HasForeignKey(x => x.RepresentationFetchId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureWarReportObservation(
        EntityTypeBuilder<WarReportObservationRow> builder)
    {
        builder.ToTable(
            "war_report_observations",
            "runtime",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_war_report_observations_total_enlistments",
                    "total_enlistments IS NULL OR total_enlistments >= 0");
                table.HasCheckConstraint(
                    "ck_war_report_observations_colonial_casualties",
                    "colonial_casualties IS NULL OR colonial_casualties >= 0");
                table.HasCheckConstraint(
                    "ck_war_report_observations_warden_casualties",
                    "warden_casualties IS NULL OR warden_casualties >= 0");
                table.HasCheckConstraint(
                    "ck_war_report_observations_day_of_war",
                    "day_of_war IS NULL OR day_of_war >= 0");
            });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.WarRegionId).HasColumnName("war_region_id");
        builder.Property(x => x.NormalizationRunId).HasColumnName("normalization_run_id");
        builder.Property(x => x.RepresentationFetchId).HasColumnName("representation_fetch_id");
        builder.Property(x => x.ObservedAt).HasColumnName("observed_at");
        builder.Property(x => x.RecordedAt)
            .HasColumnName("recorded_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();
        builder.Property(x => x.TotalEnlistments).HasColumnName("total_enlistments");
        builder.Property(x => x.ColonialCasualties).HasColumnName("colonial_casualties");
        builder.Property(x => x.WardenCasualties).HasColumnName("warden_casualties");
        builder.Property(x => x.DayOfWar).HasColumnName("day_of_war");

        builder.HasIndex(x => x.NormalizationRunId)
            .IsUnique()
            .HasDatabaseName("ux_war_report_observations_normalization_run");

        builder.HasIndex(x => new { x.WarRegionId, x.ObservedAt, x.Id })
            .HasDatabaseName("ix_war_report_observations_region_observed");

        builder.HasOne<WarRegionRow>()
            .WithMany()
            .HasForeignKey(x => x.WarRegionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<NormalizationRunRow>()
            .WithMany()
            .HasForeignKey(x => x.NormalizationRunId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FetchRow>()
            .WithMany()
            .HasForeignKey(x => x.RepresentationFetchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
