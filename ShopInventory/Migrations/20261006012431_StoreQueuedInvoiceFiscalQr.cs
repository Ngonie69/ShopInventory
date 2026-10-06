using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class StoreQueuedInvoiceFiscalQr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FiscalDayNo",
                table: "InvoiceQueue",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalQrCode",
                table: "InvoiceQueue",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalVerificationCode",
                table: "InvoiceQueue",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FiscalDayNo",
                table: "InvoiceQueue");

            migrationBuilder.DropColumn(
                name: "FiscalQrCode",
                table: "InvoiceQueue");

            migrationBuilder.DropColumn(
                name: "FiscalVerificationCode",
                table: "InvoiceQueue");
        }
    }
}
