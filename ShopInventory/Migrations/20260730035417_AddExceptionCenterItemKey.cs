using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddExceptionCenterItemKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sources keyed by Guid cannot be identified by the int ItemId, so the acknowledge,
            // assign and retry paths route on a string ItemKey instead. Every state written so far
            // belongs to an int-keyed source, and the key for those is the id in decimal, so the
            // backfill below leaves each of them addressed exactly as it was.
            migrationBuilder.DropIndex(
                name: "IX_ExceptionCenterItemStates_Source_ItemId",
                table: "ExceptionCenterItemStates");

            migrationBuilder.AddColumn<string>(
                name: "ItemKey",
                table: "ExceptionCenterItemStates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "ExceptionCenterItemStates"
                SET "ItemKey" = "ItemId"::text
                WHERE "ItemKey" IS NULL;
                """);

            // ItemId stays queryable, but it is no longer unique: Guid-keyed states all carry zero.
            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterItemStates_Source_ItemId",
                table: "ExceptionCenterItemStates",
                columns: new[] { "Source", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterItemStates_Source_ItemKey",
                table: "ExceptionCenterItemStates",
                columns: new[] { "Source", "ItemKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExceptionCenterItemStates_Source_ItemId",
                table: "ExceptionCenterItemStates");

            migrationBuilder.DropIndex(
                name: "IX_ExceptionCenterItemStates_Source_ItemKey",
                table: "ExceptionCenterItemStates");

            // Guid-keyed states cannot be told apart by ItemId — they all hold zero — so they have
            // to go before uniqueness can be restored. Only acknowledgements and assignments are
            // lost; the failed transfers themselves are untouched.
            migrationBuilder.Sql(
                """
                DELETE FROM "ExceptionCenterItemStates"
                WHERE "ItemKey" IS NULL OR "ItemKey" !~ '^[0-9]+$';
                """);

            migrationBuilder.DropColumn(
                name: "ItemKey",
                table: "ExceptionCenterItemStates");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterItemStates_Source_ItemId",
                table: "ExceptionCenterItemStates",
                columns: new[] { "Source", "ItemId" },
                unique: true);
        }
    }
}
