using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCostCentres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedCostCentres",
                columns: table => new
                {
                    CenterCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CenterName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedCostCentres", x => x.CenterCode);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedCostCentres_CenterName",
                table: "CachedCostCentres",
                column: "CenterName");

            migrationBuilder.CreateIndex(
                name: "IX_CachedCostCentres_Dimension",
                table: "CachedCostCentres",
                column: "Dimension");

            migrationBuilder.CreateIndex(
                name: "IX_CachedCostCentres_IsActive",
                table: "CachedCostCentres",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedCostCentres");
        }
    }
}
