using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultWarehouses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DataType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    IsEditable = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsSuccess = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedBusinessPartners",
                columns: table => new
                {
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CardType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    GroupCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Phone1 = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Phone2 = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Email = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Balance = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedBusinessPartners", x => x.CardCode);
                });

            migrationBuilder.CreateTable(
                name: "CachedGLAccounts",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccountType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Balance = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedGLAccounts", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "CachedPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedPrices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedWarehouses",
                columns: table => new
                {
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WarehouseName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedWarehouses", x => x.WarehouseCode);
                });

            migrationBuilder.InsertData(
                table: "CachedWarehouses",
                columns: new[] { "WarehouseCode", "City", "Country", "IsActive", "LastSyncedAt", "Location", "Street", "WarehouseName" },
                values: new object[,]
                {
                    { "01", null, null, true, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Main", null, "General Warehouse" },
                    { "02", null, null, true, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Secondary", null, "Warehouse 02" },
                    { "03", null, null, true, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Tertiary", null, "Warehouse 03" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Category",
                table: "AppSettings",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Key",
                table: "AppSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action",
                table: "AuditLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp_Username",
                table: "AuditLogs",
                columns: new[] { "Timestamp", "Username" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Username",
                table: "AuditLogs",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_CachedBusinessPartners_CardName",
                table: "CachedBusinessPartners",
                column: "CardName");

            migrationBuilder.CreateIndex(
                name: "IX_CachedBusinessPartners_CardType",
                table: "CachedBusinessPartners",
                column: "CardType");

            migrationBuilder.CreateIndex(
                name: "IX_CachedBusinessPartners_IsActive",
                table: "CachedBusinessPartners",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CachedGLAccounts_AccountType",
                table: "CachedGLAccounts",
                column: "AccountType");

            migrationBuilder.CreateIndex(
                name: "IX_CachedGLAccounts_IsActive",
                table: "CachedGLAccounts",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CachedGLAccounts_Name",
                table: "CachedGLAccounts",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_CachedPrices_Currency",
                table: "CachedPrices",
                column: "Currency");

            migrationBuilder.CreateIndex(
                name: "IX_CachedPrices_ItemCode",
                table: "CachedPrices",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_CachedPrices_ItemCode_Currency",
                table: "CachedPrices",
                columns: new[] { "ItemCode", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedWarehouses_IsActive",
                table: "CachedWarehouses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_CachedWarehouses_WarehouseName",
                table: "CachedWarehouses",
                column: "WarehouseName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "CachedBusinessPartners");

            migrationBuilder.DropTable(
                name: "CachedGLAccounts");

            migrationBuilder.DropTable(
                name: "CachedPrices");

            migrationBuilder.DropTable(
                name: "CachedWarehouses");
        }
    }
}
