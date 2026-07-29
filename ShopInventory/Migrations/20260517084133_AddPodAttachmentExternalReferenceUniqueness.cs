using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddPodAttachmentExternalReferenceUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                                UPDATE "DocumentAttachments"
                                SET "ExternalReference" = NULL
                                WHERE "ExternalReference" IS NOT NULL
                                    AND btrim("ExternalReference") = '';
                """);

            migrationBuilder.Sql(
                """
                                UPDATE "DocumentAttachments"
                                SET "ExternalReference" = btrim("ExternalReference")
                                WHERE "ExternalReference" IS NOT NULL
                                    AND "ExternalReference" <> btrim("ExternalReference");
                """);

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT
                                                "Id",
                        ROW_NUMBER() OVER (
                                                        PARTITION BY "EntityType", "EntityId", "ExternalReference"
                                                        ORDER BY "UploadedAt" ASC NULLS LAST, "Id" ASC
                        ) AS rn
                                        FROM "DocumentAttachments"
                                        WHERE "ExternalReference" IS NOT NULL
                )
                                UPDATE "DocumentAttachments" AS attachments
                                SET "ExternalReference" = NULL
                FROM ranked
                                WHERE attachments."Id" = ranked."Id"
                  AND ranked.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAttachments_EntityType_EntityId_ExternalReference",
                table: "DocumentAttachments",
                columns: new[] { "EntityType", "EntityId", "ExternalReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentAttachments_EntityType_EntityId_ExternalReference",
                table: "DocumentAttachments");
        }
    }
}
