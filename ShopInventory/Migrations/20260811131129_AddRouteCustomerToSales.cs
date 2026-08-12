using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteCustomerToSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RouteCustomerCode",
                table: "StockReservations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteCustomerId",
                table: "StockReservations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteCustomerName",
                table: "StockReservations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteCustomerCode",
                table: "SalesOrders",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteCustomerId",
                table: "SalesOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteCustomerName",
                table: "SalesOrders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteCustomerCode",
                table: "DesktopSales",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteCustomerId",
                table: "DesktopSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteCustomerName",
                table: "DesktopSales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_RouteCustomerCode",
                table: "StockReservations",
                column: "RouteCustomerCode");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_RouteCustomerId_CreatedAt",
                table: "StockReservations",
                columns: new[] { "RouteCustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_RouteCustomerCode",
                table: "SalesOrders",
                column: "RouteCustomerCode");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_RouteCustomerId_OrderDate",
                table: "SalesOrders",
                columns: new[] { "RouteCustomerId", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_RouteCustomerCode",
                table: "DesktopSales",
                column: "RouteCustomerCode");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_RouteCustomerId_DocDate",
                table: "DesktopSales",
                columns: new[] { "RouteCustomerId", "DocDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_DesktopSales_RouteCustomers_RouteCustomerId",
                table: "DesktopSales",
                column: "RouteCustomerId",
                principalTable: "RouteCustomers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesOrders_RouteCustomers_RouteCustomerId",
                table: "SalesOrders",
                column: "RouteCustomerId",
                principalTable: "RouteCustomers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_StockReservations_RouteCustomers_RouteCustomerId",
                table: "StockReservations",
                column: "RouteCustomerId",
                principalTable: "RouteCustomers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DesktopSales_RouteCustomers_RouteCustomerId",
                table: "DesktopSales");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesOrders_RouteCustomers_RouteCustomerId",
                table: "SalesOrders");

            migrationBuilder.DropForeignKey(
                name: "FK_StockReservations_RouteCustomers_RouteCustomerId",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_RouteCustomerCode",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_RouteCustomerId_CreatedAt",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_SalesOrders_RouteCustomerCode",
                table: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_SalesOrders_RouteCustomerId_OrderDate",
                table: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_DesktopSales_RouteCustomerCode",
                table: "DesktopSales");

            migrationBuilder.DropIndex(
                name: "IX_DesktopSales_RouteCustomerId_DocDate",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "RouteCustomerCode",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "RouteCustomerId",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "RouteCustomerName",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "RouteCustomerCode",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "RouteCustomerId",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "RouteCustomerName",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "RouteCustomerCode",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "RouteCustomerId",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "RouteCustomerName",
                table: "DesktopSales");
        }
    }
}
