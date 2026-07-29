using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingTransferDraftNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftNumber",
                table: "PendingInventoryTransfers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // Transfers submitted before draft numbering existed are numbered in submission
            // order, so every held draft has a reference an approver can quote.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT "Id",
                           'DT-' || to_char("CreatedAtUtc", 'YYYY') || '-' ||
                           lpad(
                               (row_number() OVER (
                                   PARTITION BY to_char("CreatedAtUtc", 'YYYY')
                                   ORDER BY "CreatedAtUtc", "Id"))::text,
                               5, '0') AS "Draft"
                    FROM "PendingInventoryTransfers"
                )
                UPDATE "PendingInventoryTransfers" AS p
                SET "DraftNumber" = n."Draft"
                FROM numbered AS n
                WHERE p."Id" = n."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryTransfers_DraftNumber",
                table: "PendingInventoryTransfers",
                column: "DraftNumber",
                unique: true,
                filter: "\"DraftNumber\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingInventoryTransfers_DraftNumber",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "DraftNumber",
                table: "PendingInventoryTransfers");
        }
    }
}
