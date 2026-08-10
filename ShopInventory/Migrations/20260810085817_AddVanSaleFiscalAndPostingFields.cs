using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanSaleFiscalAndPostingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CostCentreCode",
                table: "DesktopSales",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPostingError",
                table: "DesktopSales",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostedAt",
                table: "DesktopSales",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PostingAttempts",
                table: "DesktopSales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReceiptCounter",
                table: "DesktopSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReceiptGlobalNo",
                table: "DesktopSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SapDocEntry",
                table: "DesktopSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SapDocNum",
                table: "DesktopSales",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostCentreCode",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "LastPostingError",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PostedAt",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PostingAttempts",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "ReceiptCounter",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "ReceiptGlobalNo",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "SapDocEntry",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "SapDocNum",
                table: "DesktopSales");
        }
    }
}
