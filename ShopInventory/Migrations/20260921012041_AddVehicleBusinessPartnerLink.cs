using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleBusinessPartnerLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessPartnerCode",
                table: "TelematicsVehicles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessPartnerName",
                table: "TelematicsVehicles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelematicsVehicles_BusinessPartnerCode",
                table: "TelematicsVehicles",
                column: "BusinessPartnerCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelematicsVehicles_BusinessPartnerCode",
                table: "TelematicsVehicles");

            migrationBuilder.DropColumn(
                name: "BusinessPartnerCode",
                table: "TelematicsVehicles");

            migrationBuilder.DropColumn(
                name: "BusinessPartnerName",
                table: "TelematicsVehicles");
        }
    }
}
