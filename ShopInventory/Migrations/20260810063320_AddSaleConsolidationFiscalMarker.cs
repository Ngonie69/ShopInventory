using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleConsolidationFiscalMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConstituentFiscalReceipts",
                table: "SaleConsolidations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleConsolidations_SapDocNum",
                table: "SaleConsolidations",
                column: "SapDocNum");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SaleConsolidations_SapDocNum",
                table: "SaleConsolidations");

            migrationBuilder.DropColumn(
                name: "ConstituentFiscalReceipts",
                table: "SaleConsolidations");
        }
    }
}
