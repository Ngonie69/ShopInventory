using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedProducts",
                columns: table => new
                {
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BarCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ItemType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ManagesBatches = table.Column<bool>(type: "boolean", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    DefaultWarehouse = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UoM = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedProducts", x => x.ItemCode);
                });

            migrationBuilder.CreateTable(
                name: "CacheSyncInfo",
                columns: table => new
                {
                    CacheKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    SyncSuccessful = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheSyncInfo", x => x.CacheKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedProducts_BarCode",
                table: "CachedProducts",
                column: "BarCode");

            migrationBuilder.CreateIndex(
                name: "IX_CachedProducts_IsActive",
                table: "CachedProducts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CachedProducts_ItemName",
                table: "CachedProducts",
                column: "ItemName");

            migrationBuilder.CreateIndex(
                name: "IX_CachedProducts_LastSyncedAt",
                table: "CachedProducts",
                column: "LastSyncedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedProducts");

            migrationBuilder.DropTable(
                name: "CacheSyncInfo");
        }
    }
}
