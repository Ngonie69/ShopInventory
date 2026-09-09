using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddNegativeStockObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NegativeStockObservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ObservedOn = table.Column<DateTime>(type: "date", nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OnHand = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Committed = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NegativeStockObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NegativeStockObservations_ObservedOn",
                table: "NegativeStockObservations",
                column: "ObservedOn");

            migrationBuilder.CreateIndex(
                name: "IX_NegativeStockObservations_WarehouseCode_ItemCode",
                table: "NegativeStockObservations",
                columns: new[] { "WarehouseCode", "ItemCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NegativeStockObservations");
        }
    }
}
