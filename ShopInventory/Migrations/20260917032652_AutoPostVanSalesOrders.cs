using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AutoPostVanSalesOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPostToSap",
                table: "MobileOrderPostProcessingQueue",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoPostedAt",
                table: "MobileOrderPostProcessingQueue",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalesOrderId",
                table: "InvoiceQueue",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPostToSap",
                table: "MobileOrderPostProcessingQueue");

            migrationBuilder.DropColumn(
                name: "AutoPostedAt",
                table: "MobileOrderPostProcessingQueue");

            migrationBuilder.DropColumn(
                name: "SalesOrderId",
                table: "InvoiceQueue");
        }
    }
}
