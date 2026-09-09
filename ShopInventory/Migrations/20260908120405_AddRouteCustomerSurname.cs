using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Gives a route customer a family name, for the ones that are people.
    /// </summary>
    /// <remarks>
    /// Nullable, and left null for every existing row on purpose. The table holds two populations: a
    /// van's route customer is a shop, whose whole name is already in <c>Name</c> and is what every
    /// handset displays; a cart vendor is a person, captured at a counter as a first name and a
    /// surname. Adding a column beside <c>Name</c> leaves the shops exactly as they were, where
    /// narrowing <c>Name</c> to mean "first name" would have rewritten what is on the screen of every
    /// van in the field, for rows nobody had touched.
    /// </remarks>
    public partial class AddRouteCustomerSurname : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Surname",
                table: "RouteCustomers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Surname",
                table: "RouteCustomers");
        }
    }
}
