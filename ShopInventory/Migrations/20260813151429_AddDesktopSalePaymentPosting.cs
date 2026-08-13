using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDesktopSalePaymentPosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastPaymentError",
                table: "DesktopSales",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentPostedAt",
                table: "DesktopSales",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentSapDocEntry",
                table: "DesktopSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentSapDocNum",
                table: "DesktopSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentStatus",
                table: "DesktopSales",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPaymentError",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PaymentPostedAt",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PaymentSapDocEntry",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PaymentSapDocNum",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PaymentStatus",
                table: "DesktopSales");
        }
    }
}
