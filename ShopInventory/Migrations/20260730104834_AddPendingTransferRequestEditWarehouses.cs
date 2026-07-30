using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingTransferRequestEditWarehouses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProposedFromWarehouse",
                table: "PendingTransferRequestEdits",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedToWarehouse",
                table: "PendingTransferRequestEdits",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProposedFromWarehouse",
                table: "PendingTransferRequestEdits");

            migrationBuilder.DropColumn(
                name: "ProposedToWarehouse",
                table: "PendingTransferRequestEdits");
        }
    }
}
