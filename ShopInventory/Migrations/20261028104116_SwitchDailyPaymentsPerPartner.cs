using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Retires the one switch that turned the daily incoming payment on for every partner at once. Each
    /// partner's <c>IncomingPaymentGlMappings.IsActive</c> is now its own switch.
    /// </summary>
    /// <remarks>
    /// Without this, every active mapping would start being paid at the next 17:00 run the moment the
    /// global switch stopped being read. So unless the global switch was saved as on, every partner is
    /// switched off, which is what the business saw before: invoices posted, nothing paid. With no saved
    /// row the appsettings default (off) applied, so that counts as off too.
    /// </remarks>
    public partial class SwitchDailyPaymentsPerPartner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "IncomingPaymentGlMappings"
                SET "IsActive" = FALSE,
                    "UpdatedAtUtc" = NOW(),
                    "UpdatedBy" = 'system'
                WHERE "IsActive"
                  AND NOT EXISTS (
                      SELECT 1 FROM "SystemConfigs"
                      WHERE "Key" = 'DesktopSalePosting.DailyPaymentEnabled'
                        AND LOWER(TRIM("Value")) = 'true');
                """);

            migrationBuilder.Sql("""
                DELETE FROM "SystemConfigs" WHERE "Key" = 'DesktopSalePosting.DailyPaymentEnabled';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the per-partner switches are kept, and the old code treats a missing
            // global row as its appsettings default (off).
        }
    }
}
