using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleInvoiceCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SaleInvoiceCosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocEntry = table.Column<int>(type: "integer", nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleInvoiceCosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SaleInvoiceCostLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SaleInvoiceCostId = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Revenue = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    GrossProfit = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleInvoiceCostLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleInvoiceCostLines_SaleInvoiceCosts_SaleInvoiceCostId",
                        column: x => x.SaleInvoiceCostId,
                        principalTable: "SaleInvoiceCosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaleInvoiceCostLines_SaleInvoiceCostId",
                table: "SaleInvoiceCostLines",
                column: "SaleInvoiceCostId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleInvoiceCosts_DocEntry_WarehouseCode",
                table: "SaleInvoiceCosts",
                columns: new[] { "DocEntry", "WarehouseCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SaleInvoiceCostLines");

            migrationBuilder.DropTable(
                name: "SaleInvoiceCosts");
        }
    }
}
