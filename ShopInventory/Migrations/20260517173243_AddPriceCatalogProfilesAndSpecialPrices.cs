using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceCatalogProfilesAndSpecialPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessPartnerPriceProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Currency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PriceListNum = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncedFromSAP = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessPartnerPriceProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessPartnerSpecialPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncedFromSAP = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessPartnerSpecialPrices", x => x.Id);
                    table.CheckConstraint("CK_BusinessPartnerSpecialPrices_Price_NonNegative", "\"Price\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartnerPriceProfiles_CardCode",
                table: "BusinessPartnerPriceProfiles",
                column: "CardCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartnerPriceProfiles_IsActive",
                table: "BusinessPartnerPriceProfiles",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartnerPriceProfiles_PriceListNum",
                table: "BusinessPartnerPriceProfiles",
                column: "PriceListNum");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartnerSpecialPrices_CardCode",
                table: "BusinessPartnerSpecialPrices",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartnerSpecialPrices_CardCode_ItemCode",
                table: "BusinessPartnerSpecialPrices",
                columns: new[] { "CardCode", "ItemCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartnerSpecialPrices_IsActive",
                table: "BusinessPartnerSpecialPrices",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartnerSpecialPrices_ItemCode",
                table: "BusinessPartnerSpecialPrices",
                column: "ItemCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessPartnerPriceProfiles");

            migrationBuilder.DropTable(
                name: "BusinessPartnerSpecialPrices");
        }
    }
}
