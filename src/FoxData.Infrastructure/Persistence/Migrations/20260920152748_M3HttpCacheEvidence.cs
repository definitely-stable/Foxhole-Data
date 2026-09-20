using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3HttpCacheEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "retry_after",
                schema: "evidence",
                table: "fetches",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_age_seconds",
                schema: "evidence",
                table: "fetches",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "source_date",
                schema: "evidence",
                table: "fetches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_fetches_source_age_seconds",
                schema: "evidence",
                table: "fetches",
                sql: "source_age_seconds IS NULL OR source_age_seconds >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_fetches_source_age_seconds",
                schema: "evidence",
                table: "fetches");

            migrationBuilder.DropColumn(
                name: "retry_after",
                schema: "evidence",
                table: "fetches");

            migrationBuilder.DropColumn(
                name: "source_age_seconds",
                schema: "evidence",
                table: "fetches");

            migrationBuilder.DropColumn(
                name: "source_date",
                schema: "evidence",
                table: "fetches");
        }
    }
}
