using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveFeedCreatedAtIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The live transactions dashboard reads both tables by CreatedAt every few seconds per viewer;
            // without these each poll is a sequential scan. DesktopSales.CreatedAt is already indexed
            // (AddTillSaleAndFiscalLookupIndexes). Concurrently and outside the migration transaction for
            // the same reason as there: a plain CREATE INDEX would block the writes these tables take.
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Invoices_CreatedAt"
                ON "Invoices" ("CreatedAt");
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_IncomingPayments_CreatedAt"
                ON "IncomingPayments" ("CreatedAt");
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Invoices_CreatedAt\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_IncomingPayments_CreatedAt\";", suppressTransaction: true);
        }
    }
}
