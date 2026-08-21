using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanSalesDeclaredFiscalDayClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeclaredCloseJson",
                table: "FiscalDayStates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeclaredCloseReceivedAtUtc",
                table: "FiscalDayStates",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeclaredCloseJson",
                table: "FiscalDayStates");

            migrationBuilder.DropColumn(
                name: "DeclaredCloseReceivedAtUtc",
                table: "FiscalDayStates");
        }
    }
}
