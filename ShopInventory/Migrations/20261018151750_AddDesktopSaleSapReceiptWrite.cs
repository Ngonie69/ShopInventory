using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDesktopSaleSapReceiptWrite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SapReceiptWriteAttempts",
                table: "DesktopSales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SapReceiptWriteError",
                table: "DesktopSales",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SapReceiptWrittenAt",
                table: "DesktopSales",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SapReceiptWriteAttempts",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "SapReceiptWriteError",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "SapReceiptWrittenAt",
                table: "DesktopSales");
        }
    }
}
