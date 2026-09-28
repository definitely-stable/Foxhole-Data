using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FoxData.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FoxDataDbContext))]
[Migration("20260928120000_M6StaticMapShape")]
public sealed class M6StaticMapShape : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE evidence.map_snapshots
                ADD COLUMN source_map_items_array_present boolean
                    NOT NULL DEFAULT TRUE,
                ADD COLUMN source_map_text_items_array_present boolean
                    NOT NULL DEFAULT TRUE;

            ALTER TABLE evidence.map_snapshots
                ADD CONSTRAINT ck_map_snapshots_items_array_presence
                    CHECK (
                        source_map_items_array_present
                        OR item_count = 0),
                ADD CONSTRAINT ck_map_snapshots_text_items_array_presence
                    CHECK (
                        source_map_text_items_array_present
                        OR text_item_count = 0);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE evidence.map_snapshots
                DROP CONSTRAINT IF EXISTS
                    ck_map_snapshots_text_items_array_presence,
                DROP CONSTRAINT IF EXISTS
                    ck_map_snapshots_items_array_presence,
                DROP COLUMN IF EXISTS source_map_text_items_array_present,
                DROP COLUMN IF EXISTS source_map_items_array_present;
            """);
    }
}
