using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Creates the three trading shops and points the accounts already working their tills at them.
    /// </summary>
    /// <remarks>
    /// The shop list was compiled into the desktop app — Farm/KEFSHOP, Graniteside/KEFGRS,
    /// Machipisa/CORMACH2, in <c>LocationService.AvailableLocations</c> and again in
    /// <c>LocationSetupPage</c>. The codes below are those, and nothing else is invented: each shop's
    /// business partner and cost centre are read off the accounts already selling at that warehouse,
    /// and a warehouse whose accounts disagree about the business partner gets no shop at all rather
    /// than a guessed one. An administrator creates those on the Shops page, where the value is chosen
    /// rather than inferred.
    ///
    /// The assignment step is behaviour-preserving by construction: an account is pointed at a shop
    /// only when the shop resolves to exactly the business partner, warehouse and cost centre that
    /// account already resolves to. Nothing changes about what any till sells as — only about where
    /// the answer is stored. That is what makes this safe to run against a live database mid-deploy,
    /// alongside the per-account fallback <c>SellingAccountResolver</c> keeps.
    ///
    /// The three code columns are deliberately left on the assigned accounts. The resolver ignores
    /// them once a shop is set, and keeping them makes the Down below lossless.
    /// </remarks>
    public partial class BackfillShopsFromTillAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A warehouse is stored as a one-element JSON array on the account, which is the only
            // shape that can belong to a shop — SellingAccountResolver refuses to sell on an account
            // holding several, so a multi-warehouse account is not a till and is left alone.
            migrationBuilder.Sql("""
                WITH known_shops(code, name, warehouse) AS (
                    VALUES ('FARM', 'Farm', 'KEFSHOP'),
                           ('GRANITESIDE', 'Graniteside', 'KEFGRS'),
                           ('MACHIPISA', 'Machipisa', 'CORMACH2')
                ),
                till_accounts AS (
                    SELECT
                        upper(u."AssignedWarehouseCodes") AS warehouse_json,
                        btrim(u."AssignedBusinessPartnerCode") AS business_partner,
                        NULLIF(btrim(COALESCE(u."AssignedCostCentreCode", '')), '') AS cost_centre
                    FROM "Users" u
                    WHERE u."IsActive"
                      AND u."ShopId" IS NULL
                      AND u."AssignedBusinessPartnerCode" IS NOT NULL
                      AND btrim(u."AssignedBusinessPartnerCode") <> ''
                ),
                derived AS (
                    SELECT
                        ks.code,
                        ks.name,
                        ks.warehouse,
                        min(ta.business_partner) AS business_partner,
                        min(ta.cost_centre) AS cost_centre,
                        count(DISTINCT ta.business_partner) AS business_partner_variants,
                        count(DISTINCT COALESCE(ta.cost_centre, '')) AS cost_centre_variants
                    FROM known_shops ks
                    JOIN till_accounts ta
                      ON ta.warehouse_json = '["' || upper(ks.warehouse) || '"]'
                    GROUP BY ks.code, ks.name, ks.warehouse
                )
                INSERT INTO "Shops"
                    ("Code", "Name", "BusinessPartnerCode", "WarehouseCode", "CostCentreCode", "IsActive", "CreatedAt")
                SELECT d.code, d.name, d.business_partner, d.warehouse, d.cost_centre, TRUE, now()
                FROM derived d
                WHERE d.business_partner_variants = 1
                  AND d.cost_centre_variants = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM "Shops" s
                      WHERE upper(s."Code") = upper(d.code)
                         OR upper(s."WarehouseCode") = upper(d.warehouse));
                """);

            // Only where the shop resolves to exactly what the account already resolves to, so no
            // till changes what it sells as. An account that differs in any of the three is left on
            // its own columns for an administrator to look at.
            migrationBuilder.Sql("""
                UPDATE "Users" u
                SET "ShopId" = s."Id"
                FROM "Shops" s
                WHERE u."ShopId" IS NULL
                  AND u."IsActive"
                  AND upper(u."AssignedWarehouseCodes") = '["' || upper(s."WarehouseCode") || '"]'
                  AND upper(btrim(u."AssignedBusinessPartnerCode")) = upper(btrim(s."BusinessPartnerCode"))
                  AND COALESCE(NULLIF(btrim(u."AssignedCostCentreCode"), ''), '')
                      = COALESCE(NULLIF(btrim(COALESCE(s."CostCentreCode", '')), ''), '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossless because the assignment above never cleared the account's own columns: an
            // unassigned account falls straight back to them and resolves to the same three values.
            migrationBuilder.Sql("""
                UPDATE "Users" u
                SET "ShopId" = NULL
                FROM "Shops" s
                WHERE u."ShopId" = s."Id"
                  AND upper(s."Code") IN ('FARM', 'GRANITESIDE', 'MACHIPISA');
                """);

            // Only the seeded rows, and only while nothing points at them. A shop an administrator
            // has since created operators against is theirs, not this migration's to remove.
            migrationBuilder.Sql("""
                DELETE FROM "Shops" s
                WHERE upper(s."Code") IN ('FARM', 'GRANITESIDE', 'MACHIPISA')
                  AND NOT EXISTS (SELECT 1 FROM "Users" u WHERE u."ShopId" = s."Id");
                """);
        }
    }
}
