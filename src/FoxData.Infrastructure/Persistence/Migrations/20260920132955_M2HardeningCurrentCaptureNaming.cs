using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2HardeningCurrentCaptureNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_endpoint_state_attempts_last_authoritative_attempt_id",
                schema: "ingest",
                table: "endpoint_state");

            migrationBuilder.RenameColumn(
                name: "last_authoritative_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                newName: "last_current_capture_attempt_id");

            migrationBuilder.RenameIndex(
                name: "IX_endpoint_state_last_authoritative_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                newName: "IX_endpoint_state_last_current_capture_attempt_id");

            migrationBuilder.AddForeignKey(
                name: "FK_endpoint_state_attempts_last_current_capture_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                column: "last_current_capture_attempt_id",
                principalSchema: "ingest",
                principalTable: "attempts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_endpoint_state_attempts_last_current_capture_attempt_id",
                schema: "ingest",
                table: "endpoint_state");

            migrationBuilder.RenameColumn(
                name: "last_current_capture_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                newName: "last_authoritative_attempt_id");

            migrationBuilder.RenameIndex(
                name: "IX_endpoint_state_last_current_capture_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                newName: "IX_endpoint_state_last_authoritative_attempt_id");

            migrationBuilder.AddForeignKey(
                name: "FK_endpoint_state_attempts_last_authoritative_attempt_id",
                schema: "ingest",
                table: "endpoint_state",
                column: "last_authoritative_attempt_id",
                principalSchema: "ingest",
                principalTable: "attempts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
