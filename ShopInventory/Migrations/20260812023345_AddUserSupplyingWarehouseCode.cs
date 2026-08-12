using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSupplyingWarehouseCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupplyingWarehouseCode",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // Seed what the vans already do in practice, so no van is left unable to request stock
            // between this deploying and an admin visiting each account. The Bulawayo vans load at
            // KEFBYC; every other route loads at Graniteside. A van added later is assigned on the
            // user form, which requires the field for ADR and Sales.
            migrationBuilder.Sql("""
                UPDATE "Users"
                SET "SupplyingWarehouseCode" = CASE
                        WHEN upper(trim("AssignedBusinessPartnerCode")) IN ('VAN010', 'VAN011') THEN 'KEFBYC'
                        ELSE 'KEFGRC'
                    END
                WHERE upper("Role") IN ('ADR', 'SALES')
                  AND "SupplyingWarehouseCode" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupplyingWarehouseCode",
                table: "Users");
        }
    }
}
