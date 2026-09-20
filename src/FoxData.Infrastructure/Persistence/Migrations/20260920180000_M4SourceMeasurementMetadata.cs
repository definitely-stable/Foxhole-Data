using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M4SourceMeasurementMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "source_version",
                schema: "evidence",
                table: "source_parse_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_last_updated",
                schema: "evidence",
                table: "source_parse_runs",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_version",
                schema: "evidence",
                table: "source_parse_runs");

            migrationBuilder.DropColumn(
                name: "source_last_updated",
                schema: "evidence",
                table: "source_parse_runs");
        }
    }
}
