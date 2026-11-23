using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleInvoiceCostStalledReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SaleInvoiceCostStalledReads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DocEntryStart = table.Column<int>(type: "integer", nullable: false),
                    DocEntryEnd = table.Column<int>(type: "integer", nullable: false),
                    StatementId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StalledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleInvoiceCostStalledReads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaleInvoiceCostStalledReads_StatementId_WarehouseCode",
                table: "SaleInvoiceCostStalledReads",
                columns: new[] { "StatementId", "WarehouseCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SaleInvoiceCostStalledReads");
        }
    }
}
