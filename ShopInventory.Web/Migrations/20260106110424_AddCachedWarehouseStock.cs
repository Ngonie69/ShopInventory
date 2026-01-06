using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedWarehouseStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedWarehouseStocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BarCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InStock = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Committed = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Ordered = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Available = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UoM = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedWarehouseStocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedWarehouseStocks_BarCode",
                table: "CachedWarehouseStocks",
                column: "BarCode");

            migrationBuilder.CreateIndex(
                name: "IX_CachedWarehouseStocks_ItemCode",
                table: "CachedWarehouseStocks",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_CachedWarehouseStocks_LastSyncedAt",
                table: "CachedWarehouseStocks",
                column: "LastSyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CachedWarehouseStocks_WarehouseCode",
                table: "CachedWarehouseStocks",
                column: "WarehouseCode");

            migrationBuilder.CreateIndex(
                name: "IX_CachedWarehouseStocks_WarehouseCode_ItemCode",
                table: "CachedWarehouseStocks",
                columns: new[] { "WarehouseCode", "ItemCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedWarehouseStocks");
        }
    }
}
