using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteCustomerVisitDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RouteCustomerVisitDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RouteCustomerId = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteCustomerVisitDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteCustomerVisitDays_RouteCustomers_RouteCustomerId",
                        column: x => x.RouteCustomerId,
                        principalTable: "RouteCustomers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Seeded rather than left to the code defaults so the settings are discoverable: an
            // operator cannot edit a row that does not exist, and the fallbacks in
            // VanSalesOrderingPolicy are a safety net, not a user interface. Idempotent because a
            // re-run against a database that already holds a key must not fail the deployment.
            migrationBuilder.Sql(
                """
                INSERT INTO "SystemConfigs" ("Key", "Value", "ValueType", "Category", "Description", "IsEditable", "IsSensitive", "UpdatedAt")
                VALUES
                    ('VanSales.CustomerOrderCutOffHours', '8', 'int', 'VanSales',
                     'Hours before midnight (CAT) on a van sales customer''s visit day that app ordering closes. 8 means orders for a Tuesday call must be in by 16:00 on the Monday.',
                     TRUE, FALSE, NOW()),
                    ('VanSales.CustomerOrderPriceList', '1', 'int', 'VanSales',
                     'The SAP price list number van sales customers see in the ordering app. They are all on one list. Changing this changes what every customer is quoted.',
                     TRUE, FALSE, NOW()),
                    ('VanSales.CustomerOrderLowStockThreshold', '10', 'decimal', 'VanSales',
                     'Quantity at or below which an item shows as low stock rather than in stock in the customer ordering app. Customers never see the quantity itself, only the band.',
                     TRUE, FALSE, NOW())
                ON CONFLICT ("Key") DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RouteCustomerVisitDays_DayOfWeek",
                table: "RouteCustomerVisitDays",
                column: "DayOfWeek");

            migrationBuilder.CreateIndex(
                name: "IX_RouteCustomerVisitDays_RouteCustomerId_DayOfWeek",
                table: "RouteCustomerVisitDays",
                columns: new[] { "RouteCustomerId", "DayOfWeek" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RouteCustomerVisitDays");

            migrationBuilder.Sql(
                """
                DELETE FROM "SystemConfigs"
                WHERE "Key" IN (
                    'VanSales.CustomerOrderCutOffHours',
                    'VanSales.CustomerOrderPriceList',
                    'VanSales.CustomerOrderLowStockThreshold'
                );
                """);
        }
    }
}
