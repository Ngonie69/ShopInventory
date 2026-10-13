using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanTruckDayEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AreaName",
                table: "VanRouteDays",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OdometerUnit",
                table: "VanRouteDays",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "DailySalesTarget",
                table: "Routes",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OdometerUnit",
                table: "Routes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // What the daily workbook knew and the system did not. Matched on the seed key as well as
            // the code, because the office has corrected some codes since the routes were seeded.

            // CBD, West 1 and Bulawayo UPC read miles. Their days so far were typed in miles too.
            migrationBuilder.Sql("""
                UPDATE "Routes" SET "OdometerUnit" = 1
                WHERE "SeedKey" IN ('CBDCZA', 'WEST1', 'BYOUPC') OR "Code" IN ('CBDCZA', 'WEST1', 'BYOUPC');
                """);

            migrationBuilder.Sql("""
                UPDATE "VanRouteDays" SET "OdometerUnit" = 1
                WHERE "RouteId" IN (SELECT "Id" FROM "Routes" WHERE "OdometerUnit" = 1);
                """);

            // 3,000 a working day upcountry, 2,500 in town. Editable on the routes page.
            migrationBuilder.Sql("""
                UPDATE "Routes" SET "DailySalesTarget" = 3000
                WHERE "DeletedAt" IS NULL
                  AND ("SeedKey" IN ('UPC1', 'UPC2', 'UPC3', 'UPC4', 'BYOUPC')
                       OR "Code" IN ('UPC1', 'UPC2', 'UPC3', 'UPC4', 'BYOUPC'));
                """);

            migrationBuilder.Sql("""
                UPDATE "Routes" SET "DailySalesTarget" = 2500
                WHERE "DeletedAt" IS NULL
                  AND "DailySalesTarget" IS NULL
                  AND ("SeedKey" IN ('CBDCZA', 'EAST', 'WEST1', 'WEST2', 'BYOADR')
                       OR "Code" IN ('CBDCZA', 'EAST', 'WEST1', 'WEST2', 'BYOADR', 'BYO'));
                """);

            migrationBuilder.CreateTable(
                name: "VanTruckDayEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RouteId = table.Column<int>(type: "integer", nullable: false),
                    TradingDate = table.Column<DateTime>(type: "date", nullable: false),
                    AreaName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FuelLitres = table.Column<decimal>(type: "numeric(9,2)", nullable: true),
                    AccountSales = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanTruckDayEntries", x => x.Id);
                    table.CheckConstraint("CK_VanTruckDayEntries_AccountSales_NonNegative", "\"AccountSales\" IS NULL OR \"AccountSales\" >= 0");
                    table.CheckConstraint("CK_VanTruckDayEntries_FuelLitres_NonNegative", "\"FuelLitres\" IS NULL OR \"FuelLitres\" >= 0");
                    table.ForeignKey(
                        name: "FK_VanTruckDayEntries_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VanTruckDayEntries_RouteId_TradingDate",
                table: "VanTruckDayEntries",
                columns: new[] { "RouteId", "TradingDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanTruckDayEntries_TradingDate",
                table: "VanTruckDayEntries",
                column: "TradingDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VanTruckDayEntries");

            migrationBuilder.DropColumn(
                name: "AreaName",
                table: "VanRouteDays");

            migrationBuilder.DropColumn(
                name: "OdometerUnit",
                table: "VanRouteDays");

            migrationBuilder.DropColumn(
                name: "DailySalesTarget",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "OdometerUnit",
                table: "Routes");
        }
    }
}
