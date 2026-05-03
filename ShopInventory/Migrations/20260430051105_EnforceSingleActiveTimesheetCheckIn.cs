using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleActiveTimesheetCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                WITH duplicate_entries AS (
                    SELECT
                        "Id",
                        ROW_NUMBER() OVER (
                            PARTITION BY "UserId"
                            ORDER BY "CheckInTime" DESC, "Id" DESC
                        ) AS row_num
                    FROM "TimesheetEntries"
                    WHERE "CheckOutTime" IS NULL
                )
                UPDATE "TimesheetEntries" AS t
                SET "CheckOutTime" = t."CheckInTime",
                    "DurationMinutes" = 0,
                    "CheckOutNotes" = LEFT(
                        CASE
                            WHEN COALESCE(BTRIM(t."CheckOutNotes"), '') = '' THEN 'Auto-closed duplicate active check-in during migration.'
                            ELSE t."CheckOutNotes" || ' | Auto-closed duplicate active check-in during migration.'
                        END,
                        500
                    )
                FROM duplicate_entries AS d
                WHERE t."Id" = d."Id"
                  AND d.row_num > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_TimesheetEntries_UserId",
                table: "TimesheetEntries");

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetEntries_UserId_ActiveCheckIn",
                table: "TimesheetEntries",
                column: "UserId",
                unique: true,
                filter: "\"CheckOutTime\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TimesheetEntries_UserId_ActiveCheckIn",
                table: "TimesheetEntries");

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetEntries_UserId",
                table: "TimesheetEntries",
                column: "UserId");
        }
    }
}
