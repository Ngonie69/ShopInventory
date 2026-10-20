using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryDepotDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DriverName",
                table: "FactoryTransfers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceRequestDocEntry",
                table: "FactoryTransfers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceRequestDocNum",
                table: "FactoryTransfers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehiclePlate",
                table: "FactoryTransfers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromWarehouseCode",
                table: "FactoryTransferLines",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DriverName",
                table: "FactoryTransfers");

            migrationBuilder.DropColumn(
                name: "SourceRequestDocEntry",
                table: "FactoryTransfers");

            migrationBuilder.DropColumn(
                name: "SourceRequestDocNum",
                table: "FactoryTransfers");

            migrationBuilder.DropColumn(
                name: "VehiclePlate",
                table: "FactoryTransfers");

            migrationBuilder.DropColumn(
                name: "FromWarehouseCode",
                table: "FactoryTransferLines");
        }
    }
}
