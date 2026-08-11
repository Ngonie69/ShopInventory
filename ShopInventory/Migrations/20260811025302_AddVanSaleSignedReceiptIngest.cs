using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanSaleSignedReceiptIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceSignatureHash",
                table: "DesktopSales",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceSignatureValue",
                table: "DesktopSales",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FiscalDayOpenedAt",
                table: "DesktopSales",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PlatformReceiptId",
                table: "DesktopSales",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousReceiptHash",
                table: "DesktopSales",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceiptDate",
                table: "DesktopSales",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReceiptIngestAttempts",
                table: "DesktopSales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptIngestError",
                table: "DesktopSales",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReceiptIngestStatus",
                table: "DesktopSales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceiptIngestedAt",
                table: "DesktopSales",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HsCode",
                table: "DesktopSaleLines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TaxId",
                table: "DesktopSaleLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxPercent",
                table: "DesktopSaleLines",
                type: "numeric(5,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_ReceiptIngestStatus_ReceiptGlobalNo",
                table: "DesktopSales",
                columns: new[] { "ReceiptIngestStatus", "ReceiptGlobalNo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DesktopSales_ReceiptIngestStatus_ReceiptGlobalNo",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "DeviceSignatureHash",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "DeviceSignatureValue",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "FiscalDayOpenedAt",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PlatformReceiptId",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "PreviousReceiptHash",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "ReceiptDate",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "ReceiptIngestAttempts",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "ReceiptIngestError",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "ReceiptIngestStatus",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "ReceiptIngestedAt",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "HsCode",
                table: "DesktopSaleLines");

            migrationBuilder.DropColumn(
                name: "TaxId",
                table: "DesktopSaleLines");

            migrationBuilder.DropColumn(
                name: "TaxPercent",
                table: "DesktopSaleLines");
        }
    }
}
