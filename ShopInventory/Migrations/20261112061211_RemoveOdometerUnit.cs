using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Every odometer reading is kilometres. Drops the per-route unit and the per-day snapshot of it,
    /// which turned the readings of three trucks (CBD, West 1, Bulawayo UPC) from miles into
    /// kilometres. Their readings are now taken as kilometres, like every other truck's.
    /// </summary>
    /// <remarks>
    /// Down restores the seeded state: the three routes seeded as miles, and every day on them. A unit
    /// the office changed on the routes page after seeding is not recoverable, because it is dropped.
    /// </remarks>
    public partial class RemoveOdometerUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OdometerUnit",
                table: "VanRouteDays");

            migrationBuilder.DropColumn(
                name: "OdometerUnit",
                table: "Routes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OdometerUnit",
                table: "VanRouteDays",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OdometerUnit",
                table: "Routes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 1 was Miles. The seed keys are the codes the routes were seeded with.
            migrationBuilder.Sql(
                """
                UPDATE "Routes" SET "OdometerUnit" = 1 WHERE "SeedKey" IN ('CBDCZA', 'WEST1', 'BYOUPC');
                UPDATE "VanRouteDays" AS d SET "OdometerUnit" = 1
                FROM "Routes" AS r
                WHERE d."RouteId" = r."Id" AND r."OdometerUnit" = 1;
                """);
        }
    }
}
