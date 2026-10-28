using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Takes off every Manager account the write permissions the Manager role stopped carrying on
    /// 2026-10-28, when the role became read-only: raising, editing, approving and receiving
    /// purchasing documents, transferring and adjusting stock, posting write-offs, approving sales
    /// orders and SAP credit memos, adding approved credit notes, creating and editing products and
    /// customers, fulfilling van customer orders and confirming market breakages.
    /// </summary>
    /// <remarks>
    /// Same reason and recipe as <see cref="RemoveManagerSetupPermissions"/>: user creation copies the
    /// role's defaults onto the account's own <c>Permissions</c> column and effective permissions are
    /// the two joined, so changing the role alone reaches no manager created before the change. The
    /// list is written out rather than read from the role, so the migration means the same thing
    /// whenever it runs, and it is exactly the codes taken off the Manager defaults that day. A code an
    /// administrator granted one manager on purpose goes too; it can be granted again on the User
    /// Management page. Row by row, so a column that is not a JSON array is skipped, not fatal.
    ///
    /// Down restores nothing: putting write rights back on every manager is not something a rollback
    /// should do silently.
    /// </remarks>
    public partial class RemoveManagerWritePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    removed constant text[] := ARRAY[
                        'products.create', 'products.edit', 'products.manage_prices',
                        'stock.transfer', 'inventory.transfer', 'stock.adjust', 'inventory.adjust',
                        'stock.writeoffs.post',
                        'purchasing.create', 'purchasing.edit', 'purchasing.approve', 'purchasing.receive',
                        'purchasing.requests.create', 'purchasing.quotations.create',
                        'purchasing.grpo.create', 'purchasing.invoices.create',
                        'salesorders.approve',
                        'creditnotes.approve', 'creditnotes.add_approved',
                        'customers.create', 'customers.edit',
                        'vansales.customer_orders.fulfil', 'vansales.breakages.confirm'];
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
