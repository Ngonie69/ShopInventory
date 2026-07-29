using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddSapItemUomMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SapItemUomMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestedUomCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UoMCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UoMEntry = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SapItemUomMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SapItemUomMappings_ItemCode_RequestedUomCode",
                table: "SapItemUomMappings",
                columns: new[] { "ItemCode", "RequestedUomCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SapItemUomMappings");
        }
    }
}
