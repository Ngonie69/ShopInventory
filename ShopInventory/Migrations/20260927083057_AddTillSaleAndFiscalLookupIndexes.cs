using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddTillSaleAndFiscalLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Concurrently, outside the migration transaction: both tables take a write per till sale or
            // fiscalised document, and a plain CREATE INDEX would block those writes while it builds.
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DesktopSales_CreatedAt"
                ON "DesktopSales" ("CreatedAt");
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DesktopFiscalTransactions_DocumentType_DocNum"
                ON "DesktopFiscalTransactions" ("DocumentType", "DocNum");
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_DesktopSales_CreatedAt\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_DesktopFiscalTransactions_DocumentType_DocNum\";", suppressTransaction: true);
        }
    }
}
