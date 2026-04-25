using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceHotspotIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_Username_Lower"
                ON "Users" (lower("Username"));
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_Email_Lower"
                ON "Users" (lower("Email"))
                WHERE "Email" IS NOT NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_Username_Trgm"
                ON "Users" USING gin (lower("Username") gin_trgm_ops);
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_Email_Trgm"
                ON "Users" USING gin (lower("Email") gin_trgm_ops)
                WHERE "Email" IS NOT NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_FirstName_Trgm"
                ON "Users" USING gin (lower("FirstName") gin_trgm_ops)
                WHERE "FirstName" IS NOT NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_LastName_Trgm"
                ON "Users" USING gin (lower("LastName") gin_trgm_ops)
                WHERE "LastName" IS NOT NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DocumentAttachments_FileName_Trgm"
                ON "DocumentAttachments" USING gin (lower("FileName") gin_trgm_ops);
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DocumentAttachments_Description_Trgm"
                ON "DocumentAttachments" USING gin (lower("Description") gin_trgm_ops)
                WHERE "Description" IS NOT NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_MerchandiserProducts_ItemCode_Trgm"
                ON "MerchandiserProducts" USING gin (lower("ItemCode") gin_trgm_ops);
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_MerchandiserProducts_ItemName_Trgm"
                ON "MerchandiserProducts" USING gin (lower("ItemName") gin_trgm_ops)
                WHERE "ItemName" IS NOT NULL;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_PasswordResetTokens_RequestedByIp_CreatedAt"
                ON "PasswordResetTokens" ("RequestedByIp", "CreatedAt");
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_PasswordResetTokens_UserId_IsUsed"
                ON "PasswordResetTokens" ("UserId", "IsUsed");
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_PasswordResetTokens_UserId_IsUsed\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_PasswordResetTokens_RequestedByIp_CreatedAt\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_MerchandiserProducts_ItemName_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_MerchandiserProducts_ItemCode_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_DocumentAttachments_Description_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_DocumentAttachments_FileName_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Users_LastName_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Users_FirstName_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Users_Email_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Users_Username_Trgm\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Users_Email_Lower\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"IX_Users_Username_Lower\";", suppressTransaction: true);
        }
    }
}
