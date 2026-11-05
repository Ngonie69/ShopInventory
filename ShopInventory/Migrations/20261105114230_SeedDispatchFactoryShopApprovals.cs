using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Holds transfers between Dispatch and the factory shop (KEFSHOP) for the stock officer at the
    /// receiving end: the shop's officer for stock coming in, each dispatch warehouse's officer for
    /// stock going back.
    /// </summary>
    /// <remarks>
    /// Seeded once here rather than among the engine's built-in defaults, which come back whenever
    /// they are missing: these name this company's warehouses, so an administrator who deletes or
    /// renames them should be able to. Each insert is skipped when its name is already taken.
    /// </remarks>
    public partial class SeedDispatchFactoryShopApprovals : Migration
    {
        private const string StageId = "11000000-0000-0000-0000-000000000004";
        private const string ToShopTemplateId = "22000000-0000-0000-0000-000000000014";
        private const string FromShopTemplateId = "22000000-0000-0000-0000-000000000015";
        private const string Dispatch = """["DISPCH","DISPYOG","DISPMILK","DISPICE"]""";
        private const string FactoryShop = """["KEFSHOP"]""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                INSERT INTO "ApprovalStageDefinitions"
                    ("Id", "Name", "Description", "ApprovalsRequired", "RejectionsRequired",
                     "AuthorizerUserIdsJson", "AuthorizerRolesJson", "WarehouseScope", "ApprovalPerWarehouse",
                     "IsActive", "CreatedAtUtc")
                SELECT '{StageId}', 'Receiving stock officer',
                       'The stock officer at the warehouse the stock is going to. A transfer to several warehouses needs one from each.',
                       1, 1, '[]', '["StockController"]', 'Destination', TRUE, TRUE, now()
                WHERE NOT EXISTS (SELECT 1 FROM "ApprovalStageDefinitions" WHERE "Name" = 'Receiving stock officer');
                """);

            // The templates point at whichever stage holds the name, which is the one just added
            // unless an install already had a stage of its own by that name.
            InsertTemplate(migrationBuilder, ToShopTemplateId, "Dispatch to Factory Shop",
                "Dispatch stock going into the factory shop waits for the shop's stock officer.", Dispatch, FactoryShop);
            InsertTemplate(migrationBuilder, FromShopTemplateId, "Factory Shop returns to Dispatch",
                "Factory shop stock going back to Dispatch waits for each receiving dispatch warehouse's stock officer.", FactoryShop, Dispatch);
        }

        private static void InsertTemplate(
            MigrationBuilder migrationBuilder, string id, string name, string description, string from, string to)
            => migrationBuilder.Sql($"""
                INSERT INTO "ApprovalTemplateDefinitions"
                    ("Id", "Name", "Description", "DocumentType", "OriginatorUserIdsJson", "OriginatorRolesJson",
                     "StageIdsJson", "FromWarehousesJson", "ToWarehousesJson", "Priority", "IsActive", "CreatedAtUtc")
                SELECT '{id}', '{name}', '{description.Replace("'", "''")}', 'InventoryTransfer', '[]', '[]',
                       json_build_array(stage."Id")::text, '{from}', '{to}', 200, TRUE, now()
                FROM "ApprovalStageDefinitions" stage
                WHERE stage."Name" = 'Receiving stock officer'
                  AND NOT EXISTS (SELECT 1 FROM "ApprovalTemplateDefinitions" WHERE "Name" = '{name}');
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the rows this migration added, and only while no approval has been opened against
            // them; once one has, the template is history and stays.
            migrationBuilder.Sql($"""
                DELETE FROM "ApprovalTemplateDefinitions" template
                WHERE template."Id" IN ('{ToShopTemplateId}', '{FromShopTemplateId}')
                  AND NOT EXISTS (SELECT 1 FROM "ApprovalRequests" request WHERE request."ApprovalTemplateId" = template."Id");

                DELETE FROM "ApprovalStageDefinitions" stage
                WHERE stage."Id" = '{StageId}'
                  AND NOT EXISTS (SELECT 1 FROM "ApprovalTemplateDefinitions" template WHERE template."StageIdsJson" LIKE '%' || stage."Id"::text || '%');
                """);
        }
    }
}
