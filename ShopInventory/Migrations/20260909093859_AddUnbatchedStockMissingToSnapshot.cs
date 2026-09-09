using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddUnbatchedStockMissingToSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UnbatchedStockMissing",
                table: "DailyStockSnapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill, so a snapshot already sitting in this state today can be repaired rather
            // than having to wait for tomorrow's 07:00 fetch. Rows written before the flag existed
            // have to be recognised from what they left behind: Complete (1), a recorded gap, and
            // not one batchless row to show for it. That inference is only good enough for rows
            // that predate the flag — from here on the fetch sets it outright — and the worst it
            // can cost is one extra unbatched read for a warehouse that genuinely holds none.
            migrationBuilder.Sql(@"
                UPDATE ""DailyStockSnapshots"" AS s
                SET ""UnbatchedStockMissing"" = true
                WHERE s.""Status"" = 1
                  AND s.""LastError"" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ""DailyStockSnapshotItems"" AS i
                      WHERE i.""SnapshotId"" = s.""Id""
                        AND i.""BatchNumber"" IS NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnbatchedStockMissing",
                table: "DailyStockSnapshots");
        }
    }
}
