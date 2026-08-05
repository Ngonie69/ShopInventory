using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddItemAndBusinessPartnerGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ItemsGroupCode",
                table: "CachedProducts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CachedBusinessPartnerGroups",
                columns: table => new
                {
                    Code = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedBusinessPartnerGroups", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "CachedItemGroups",
                columns: table => new
                {
                    Number = table.Column<int>(type: "integer", nullable: false),
                    GroupName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedItemGroups", x => x.Number);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedBusinessPartnerGroups_Name",
                table: "CachedBusinessPartnerGroups",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_CachedItemGroups_GroupName",
                table: "CachedItemGroups",
                column: "GroupName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedBusinessPartnerGroups");

            migrationBuilder.DropTable(
                name: "CachedItemGroups");

            migrationBuilder.DropColumn(
                name: "ItemsGroupCode",
                table: "CachedProducts");
        }
    }
}
