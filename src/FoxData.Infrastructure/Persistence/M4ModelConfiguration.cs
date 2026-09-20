using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoxData.Infrastructure.Persistence;

internal static class M4ModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureSourceScheduleDecision(
            modelBuilder.Entity<SourceScheduleDecisionRow>());
    }

    private static void ConfigureSourceScheduleDecision(
        EntityTypeBuilder<SourceScheduleDecisionRow> builder)
    {
        builder.ToTable(
            "source_schedule_decisions",
            "evidence",
            table => table.HasCheckConstraint(
                "ck_source_schedule_decisions_effective_cadence_ms",
                "effective_cadence_ms > 0"));

        builder.HasKey(x => x.FetchId);

        builder.Property(x => x.FetchId)
            .HasColumnName("fetch_id")
            .ValueGeneratedNever();
        builder.Property(x => x.EndpointId)
            .HasColumnName("endpoint_id");
        builder.Property(x => x.PolicyVersion)
            .HasColumnName("policy_version")
            .HasMaxLength(128);
        builder.Property(x => x.EffectiveCadenceMs)
            .HasColumnName("effective_cadence_ms");
        builder.Property(x => x.EndpointActive)
            .HasColumnName("endpoint_active");
        builder.Property(x => x.ProbeSelected)
            .HasColumnName("probe_selected");
        builder.Property(x => x.SourceCacheEligibleAt)
            .HasColumnName("source_cache_eligible_at");
        builder.Property(x => x.NextTargetAt)
            .HasColumnName("next_target_at");
        builder.Property(x => x.RetryEligibleAt)
            .HasColumnName("retry_eligible_at");
        builder.Property(x => x.SuccessorJobId)
            .HasColumnName("successor_job_id");
        builder.Property(x => x.SuccessorAvailableAt)
            .HasColumnName("successor_available_at");
        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("transaction_timestamp()")
            .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.EndpointId)
            .HasDatabaseName(
                "ix_source_schedule_decisions_endpoint");

        builder.HasIndex(x => x.SuccessorJobId)
            .IsUnique()
            .HasDatabaseName(
                "ux_source_schedule_decisions_successor_job");

        builder.HasOne<FetchRow>()
            .WithOne()
            .HasForeignKey<SourceScheduleDecisionRow>(
                x => x.FetchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<EndpointRow>()
            .WithMany()
            .HasForeignKey(x => x.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CollectionJobRow>()
            .WithMany()
            .HasForeignKey(x => x.SuccessorJobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
