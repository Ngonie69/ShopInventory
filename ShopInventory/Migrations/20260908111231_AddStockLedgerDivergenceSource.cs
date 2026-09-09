using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddStockLedgerDivergenceSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reference",
                table: "StockLedgerDivergences",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "StockLedgerDivergences",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_StockLedgerDivergences_Source",
                table: "StockLedgerDivergences",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockLedgerDivergences_Source",
                table: "StockLedgerDivergences");

            migrationBuilder.DropColumn(
                name: "Reference",
                table: "StockLedgerDivergences");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "StockLedgerDivergences");
        }
    }
}
