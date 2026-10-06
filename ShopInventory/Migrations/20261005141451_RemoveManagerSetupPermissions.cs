using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Takes off every Manager account the permissions the Manager role no longer carries: user
    /// accounts, settings, the audit trail, sync and selling-route set-up, which are the
    /// administrator's, and raising, editing, voiding or posting invoices, payments, sales orders and
    /// quotations, which a manager now reads without doing.
    /// </summary>
    /// <remarks>
    /// Changing <c>Permissions.GetDefaultPermissionsForRole</c> is not enough on its own. Creating a
    /// user writes the role's defaults onto the account's own <c>Permissions</c> column, and a user's
    /// effective permissions are the role's defaults joined with that column — so every manager created
    /// before the change would keep each code dropped from the role, for good.
    ///
    /// The list is written out here rather than read from the role, because a migration has to mean
    /// the same thing whenever it runs. It is exactly the codes taken off the Manager defaults on
    /// 2026-10-05.
    ///
    /// A code an administrator granted one manager on purpose cannot be told apart from one the
    /// creation copied, so both go. The administrator can grant it again on the User Management page.
    ///
    /// Row by row, so an account whose column is not a JSON array is skipped instead of failing the
    /// migration: Postgres does not promise to evaluate a WHERE clause in the order it is written, so a
    /// single UPDATE guarding the cast with a LIKE could still meet the bad row in the cast.
    ///
    /// Down restores nothing: putting administration rights back on every manager is not something a
    /// rollback should do silently.
    /// </remarks>
    public partial class RemoveManagerSetupPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    removed constant text[] := ARRAY[
                        'users.view', 'settings.view', 'settings.edit', 'audit.view', 'sync.view',
                        'vansales.routes.manage',
                        'invoices.create', 'invoices.edit', 'invoices.void',
                        'payments.create', 'payments.refund', 'payments.process_refunds',
                        'salesorders.create', 'salesorders.edit', 'salesorders.delete', 'salesorders.post_to_sap',
                        'quotations.create', 'quotations.edit'];
                    account record;
                    held json;
                    kept text;
                BEGIN
                    FOR account IN
                        SELECT "Id", "Permissions"
                        FROM "Users"
                        WHERE lower(btrim("Role")) = 'manager'
                          AND "Permissions" IS NOT NULL
                    LOOP
                        BEGIN
                            held := account."Permissions"::json;
                        EXCEPTION WHEN invalid_text_representation THEN
                            RAISE NOTICE 'Manager % has a Permissions value that is not JSON; left as it is', account."Id";
                            CONTINUE;
                        END;

                        CONTINUE WHEN json_typeof(held) <> 'array';
                        CONTINUE WHEN NOT EXISTS (
                            SELECT 1 FROM json_array_elements_text(held) AS code WHERE code = ANY (removed));

                        SELECT COALESCE(json_agg(p.code ORDER BY p.position), '[]'::json)::text
                        INTO kept
                        FROM json_array_elements_text(held) WITH ORDINALITY AS p(code, position)
                        WHERE NOT (p.code = ANY (removed));

                        UPDATE "Users"
                        SET "Permissions" = kept,
                            "UpdatedAt" = now()
                        WHERE "Id" = account."Id";
                    END LOOP;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
