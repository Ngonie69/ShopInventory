using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelToCachedBusinessPartners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "CachedBusinessPartners",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channel",
                table: "CachedBusinessPartners");
        }
    }
}
