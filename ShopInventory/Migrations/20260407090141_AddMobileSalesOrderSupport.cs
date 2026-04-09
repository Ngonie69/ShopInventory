using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileSalesOrderSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedCustomerCodes",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceInfo",
                table: "SalesOrders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchandiserNotes",
                table: "SalesOrders",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "SalesOrders",
                type: "bytea",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "SalesOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CardCode_Status_OrderDate",
                table: "SalesOrders",
                columns: new[] { "CardCode", "Status", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CardCode_Status_OrderDate",
                table: "PurchaseOrders",
                columns: new[] { "CardCode", "Status", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CardCode_Status_DocDate",
                table: "Invoices",
                columns: new[] { "CardCode", "Status", "DocDate" });

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPayments_CardCode_Status_DocDate",
                table: "IncomingPayments",
                columns: new[] { "CardCode", "Status", "DocDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_CardCode_Status_CreditNoteDate",
                table: "CreditNotes",
                columns: new[] { "CardCode", "Status", "CreditNoteDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SalesOrders_CardCode_Status_OrderDate",
                table: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CardCode_Status_OrderDate",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_CardCode_Status_DocDate",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_IncomingPayments_CardCode_Status_DocDate",
                table: "IncomingPayments");

            migrationBuilder.DropIndex(
                name: "IX_CreditNotes_CardCode_Status_CreditNoteDate",
                table: "CreditNotes");

            migrationBuilder.DropColumn(
                name: "AssignedCustomerCodes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeviceInfo",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "MerchandiserNotes",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "SalesOrders");
        }
    }
}
