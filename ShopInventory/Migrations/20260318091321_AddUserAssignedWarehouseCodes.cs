using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAssignedWarehouseCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add the new column
            migrationBuilder.AddColumn<string>(
                name: "AssignedWarehouseCodes",
                table: "Users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // 2. Migrate existing data: convert single warehouse code to JSON array
            migrationBuilder.Sql(
                """
                UPDATE "Users"
                SET "AssignedWarehouseCodes" = '["' || "AssignedWarehouseCode" || '"]'
                WHERE "AssignedWarehouseCode" IS NOT NULL AND "AssignedWarehouseCode" != '';
                """);

            // 3. Drop the old column
            migrationBuilder.DropColumn(
                name: "AssignedWarehouseCode",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedWarehouseCodes",
                table: "Users");

            migrationBuilder.AddColumn<string>(
                name: "AssignedWarehouseCode",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
