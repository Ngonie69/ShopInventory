using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteTemperatureLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureMaxC",
                table: "Routes",
                type: "numeric(4,1)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureMinC",
                table: "Routes",
                type: "numeric(4,1)",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "TemperatureProbeChannel",
                table: "Routes",
                type: "smallint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TemperatureMaxC",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "TemperatureMinC",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "TemperatureProbeChannel",
                table: "Routes");
        }
    }
}
