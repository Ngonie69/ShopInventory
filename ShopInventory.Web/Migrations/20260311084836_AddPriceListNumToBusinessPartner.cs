using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceListNumToBusinessPartner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PriceListNum",
                table: "CachedBusinessPartners",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceListNum",
                table: "CachedBusinessPartners");
        }
    }
}
