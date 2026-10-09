using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AllowVanSaleDocumentDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomerDocumentDeliveries_OneDocument",
                table: "CustomerDocumentDeliveries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomerDocumentDeliveries_HasDocument",
                table: "CustomerDocumentDeliveries",
                sql: "\"SapDocEntry\" IS NOT NULL OR \"DesktopSaleId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomerDocumentDeliveries_HasDocument",
                table: "CustomerDocumentDeliveries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomerDocumentDeliveries_OneDocument",
                table: "CustomerDocumentDeliveries",
                sql: "(\"SapDocEntry\" IS NULL) <> (\"DesktopSaleId\" IS NULL)");
        }
    }
}
