using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddStockLedgerDivergences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockLedgerDivergences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LedgerDay = table.Column<DateTime>(type: "date", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LedgerQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    SapIssuableQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Difference = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLedgerDivergences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockLedgerDivergences_CheckedAt",
                table: "StockLedgerDivergences",
                column: "CheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockLedgerDivergences_WarehouseCode_ItemCode",
                table: "StockLedgerDivergences",
                columns: new[] { "WarehouseCode", "ItemCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockLedgerDivergences");
        }
    }
}
