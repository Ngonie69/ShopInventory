using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Gives Dispatch stock going into the factory shop (KEFSHOP) to the shop's stock user instead of
    /// a stock officer. The two are different people: the shop's stock user holds the ShopStock role,
    /// and stock officers keep the shop's returns to Dispatch.
    /// </summary>
    /// <remarks>
    /// SeedDispatchFactoryShopApprovals pointed both factory shop templates at one stage, "Receiving
    /// stock officer", which only a StockController can decide. This adds a "Factory shop stock" stage
    /// for the ShopStock role at the receiving warehouse and moves "Dispatch to Factory Shop" onto it.
    ///
    /// The template is moved only while it still holds exactly the stage it was seeded with, so an
    /// administrator's own arrangement is left alone. Approvals already opened keep the stages they
    /// were opened with; an administrator can decide those.
    /// </remarks>
    public partial class SeparateFactoryShopStockApproval : Migration
    {
        private const string ShopStockStageId = "11000000-0000-0000-0000-000000000005";
        private const string ShopStockStageName = "Factory shop stock";
        private const string ReceivingOfficerStageName = "Receiving stock officer";
        private const string ToShopTemplateId = "22000000-0000-0000-0000-000000000014";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                INSERT INTO "ApprovalStageDefinitions"
                    ("Id", "Name", "Description", "ApprovalsRequired", "RejectionsRequired",
                     "AuthorizerUserIdsJson", "AuthorizerRolesJson", "WarehouseScope", "ApprovalPerWarehouse",
                     "IsActive", "CreatedAtUtc")
                SELECT '{ShopStockStageId}', '{ShopStockStageName}',
                       'The shop''s stock user at the warehouse the stock is going to. Not a stock officer.',
                       1, 1, '[]', '["ShopStock"]', 'Destination', TRUE, TRUE, now()
                WHERE NOT EXISTS (SELECT 1 FROM "ApprovalStageDefinitions" WHERE "Name" = '{ShopStockStageName}');

                UPDATE "ApprovalTemplateDefinitions" template
                SET "StageIdsJson" = json_build_array(shop."Id")::text,
                    "Description" = 'Dispatch stock going into the factory shop waits for the shop''s stock user.',
                    "UpdatedAtUtc" = now()
                FROM "ApprovalStageDefinitions" shop, "ApprovalStageDefinitions" officer
                WHERE template."Id" = '{ToShopTemplateId}'
                  AND shop."Name" = '{ShopStockStageName}'
                  AND officer."Name" = '{ReceivingOfficerStageName}'
                  AND template."StageIdsJson"::jsonb = jsonb_build_array(officer."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back onto the stock officer's stage only while the template still holds just the shop
            // stage, and the stage goes only once nothing points at it or was decided under it.
            migrationBuilder.Sql($"""
                UPDATE "ApprovalTemplateDefinitions" template
                SET "StageIdsJson" = json_build_array(officer."Id")::text,
                    "Description" = 'Dispatch stock going into the factory shop waits for the shop''s stock officer.',
                    "UpdatedAtUtc" = now()
                FROM "ApprovalStageDefinitions" shop, "ApprovalStageDefinitions" officer
                WHERE template."Id" = '{ToShopTemplateId}'
                  AND shop."Id" = '{ShopStockStageId}'
                  AND officer."Name" = '{ReceivingOfficerStageName}'
                  AND template."StageIdsJson"::jsonb = jsonb_build_array(shop."Id");

                DELETE FROM "ApprovalStageDefinitions" stage
                WHERE stage."Id" = '{ShopStockStageId}'
                  AND NOT EXISTS (SELECT 1 FROM "ApprovalTemplateDefinitions" template WHERE template."StageIdsJson" LIKE '%' || stage."Id"::text || '%')
                  AND NOT EXISTS (SELECT 1 FROM "ApprovalDecisions" decision WHERE decision."StageId" = stage."Id");
                """);
        }
    }
}
