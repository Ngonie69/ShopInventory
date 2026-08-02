using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAttachmentContentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentSha256",
                table: "DocumentAttachments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAttachments_EntityType_EntityId_ContentSha256",
                table: "DocumentAttachments",
                columns: new[] { "EntityType", "EntityId", "ContentSha256" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentAttachments_EntityType_EntityId_ContentSha256",
                table: "DocumentAttachments");

            migrationBuilder.DropColumn(
                name: "ContentSha256",
                table: "DocumentAttachments");
        }
    }
}
