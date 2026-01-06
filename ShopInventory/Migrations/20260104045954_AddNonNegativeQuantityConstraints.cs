using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddNonNegativeQuantityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_QuantityOnStock_NonNegative",
                table: "Products",
                sql: "\"QuantityOnStock\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_QuantityOrderedByCustomers_NonNegative",
                table: "Products",
                sql: "\"QuantityOrderedByCustomers\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_QuantityOrderedFromVendors_NonNegative",
                table: "Products",
                sql: "\"QuantityOrderedFromVendors\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductBatches_Quantity_NonNegative",
                table: "ProductBatches",
                sql: "\"Quantity\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ItemPrices_Price_NonNegative",
                table: "ItemPrices",
                sql: "\"Price\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Invoices_DocTotal_NonNegative",
                table: "Invoices",
                sql: "\"DocTotal\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Invoices_VatSum_NonNegative",
                table: "Invoices",
                sql: "\"VatSum\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InvoiceLines_DiscountPercent_Valid",
                table: "InvoiceLines",
                sql: "\"DiscountPercent\" >= 0 AND \"DiscountPercent\" <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InvoiceLines_LineTotal_NonNegative",
                table: "InvoiceLines",
                sql: "\"LineTotal\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InvoiceLines_Quantity_Positive",
                table: "InvoiceLines",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InvoiceLines_UnitPrice_NonNegative",
                table: "InvoiceLines",
                sql: "\"UnitPrice\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InvoiceLineBatches_Quantity_Positive",
                table: "InvoiceLineBatches",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryTransferLines_Quantity_Positive",
                table: "InventoryTransferLines",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryTransferLineBatches_Quantity_Positive",
                table: "InventoryTransferLineBatches",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPayments_CashSum_NonNegative",
                table: "IncomingPayments",
                sql: "\"CashSum\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPayments_CheckSum_NonNegative",
                table: "IncomingPayments",
                sql: "\"CheckSum\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPayments_CreditSum_NonNegative",
                table: "IncomingPayments",
                sql: "\"CreditSum\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPayments_DocTotal_NonNegative",
                table: "IncomingPayments",
                sql: "\"DocTotal\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPayments_TransferSum_NonNegative",
                table: "IncomingPayments",
                sql: "\"TransferSum\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPaymentInvoices_SumApplied_NonNegative",
                table: "IncomingPaymentInvoices",
                sql: "\"SumApplied\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPaymentCreditCards_CreditSum_NonNegative",
                table: "IncomingPaymentCreditCards",
                sql: "\"CreditSum\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IncomingPaymentChecks_CheckSum_NonNegative",
                table: "IncomingPaymentChecks",
                sql: "\"CheckSum\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_QuantityOnStock_NonNegative",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_QuantityOrderedByCustomers_NonNegative",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_QuantityOrderedFromVendors_NonNegative",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductBatches_Quantity_NonNegative",
                table: "ProductBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ItemPrices_Price_NonNegative",
                table: "ItemPrices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Invoices_DocTotal_NonNegative",
                table: "Invoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Invoices_VatSum_NonNegative",
                table: "Invoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InvoiceLines_DiscountPercent_Valid",
                table: "InvoiceLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InvoiceLines_LineTotal_NonNegative",
                table: "InvoiceLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InvoiceLines_Quantity_Positive",
                table: "InvoiceLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InvoiceLines_UnitPrice_NonNegative",
                table: "InvoiceLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InvoiceLineBatches_Quantity_Positive",
                table: "InvoiceLineBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryTransferLines_Quantity_Positive",
                table: "InventoryTransferLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryTransferLineBatches_Quantity_Positive",
                table: "InventoryTransferLineBatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPayments_CashSum_NonNegative",
                table: "IncomingPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPayments_CheckSum_NonNegative",
                table: "IncomingPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPayments_CreditSum_NonNegative",
                table: "IncomingPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPayments_DocTotal_NonNegative",
                table: "IncomingPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPayments_TransferSum_NonNegative",
                table: "IncomingPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPaymentInvoices_SumApplied_NonNegative",
                table: "IncomingPaymentInvoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPaymentCreditCards_CreditSum_NonNegative",
                table: "IncomingPaymentCreditCards");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IncomingPaymentChecks_CheckSum_NonNegative",
                table: "IncomingPaymentChecks");
        }
    }
}
