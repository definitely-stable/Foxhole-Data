using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3BodyErrorEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "body_error_code",
                schema: "evidence",
                table: "fetches",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "body_error_code",
                schema: "evidence",
                table: "fetches");
        }
    }
}
