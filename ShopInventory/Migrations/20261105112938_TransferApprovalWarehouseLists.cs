using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Lets an approval template name several warehouses on each end, and an approval stage be
    /// limited to authorizers assigned to the transfer's source or destination.
    /// </summary>
    /// <remarks>
    /// The template's single From/To codes are carried into the new lists before the old columns go,
    /// so a template that matched one warehouse still matches exactly that warehouse.
    /// </remarks>
    public partial class TransferApprovalWarehouseLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FromWarehousesJson",
                table: "ApprovalTemplateDefinitions",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ToWarehousesJson",
                table: "ApprovalTemplateDefinitions",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql("""
                UPDATE "ApprovalTemplateDefinitions"
                SET "FromWarehousesJson" = json_build_array(btrim("FromWarehouse"))::text
                WHERE "FromWarehouse" IS NOT NULL AND btrim("FromWarehouse") <> '';

                UPDATE "ApprovalTemplateDefinitions"
                SET "ToWarehousesJson" = json_build_array(btrim("ToWarehouse"))::text
                WHERE "ToWarehouse" IS NOT NULL AND btrim("ToWarehouse") <> '';
                """);

            migrationBuilder.DropColumn(
                name: "FromWarehouse",
                table: "ApprovalTemplateDefinitions");

            migrationBuilder.DropColumn(
                name: "ToWarehouse",
                table: "ApprovalTemplateDefinitions");

            migrationBuilder.AddColumn<bool>(
                name: "ApprovalPerWarehouse",
                table: "ApprovalStageDefinitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WarehouseScope",
                table: "ApprovalStageDefinitions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Any");

            migrationBuilder.AlterColumn<string>(
                name: "ToWarehouse",
                table: "ApprovalRequests",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FromWarehouse",
                table: "ApprovalRequests",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WarehouseCodesJson",
                table: "ApprovalDecisions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FromWarehouse",
                table: "ApprovalTemplateDefinitions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToWarehouse",
                table: "ApprovalTemplateDefinitions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // A single column holds one code, so a list keeps only its first.
            migrationBuilder.Sql("""
                UPDATE "ApprovalTemplateDefinitions"
                SET "FromWarehouse" = left("FromWarehousesJson"::jsonb ->> 0, 50),
                    "ToWarehouse" = left("ToWarehousesJson"::jsonb ->> 0, 50);
                """);

            migrationBuilder.DropColumn(
                name: "FromWarehousesJson",
                table: "ApprovalTemplateDefinitions");

            migrationBuilder.DropColumn(
                name: "ToWarehousesJson",
                table: "ApprovalTemplateDefinitions");

            migrationBuilder.DropColumn(
                name: "ApprovalPerWarehouse",
                table: "ApprovalStageDefinitions");

            migrationBuilder.DropColumn(
                name: "WarehouseScope",
                table: "ApprovalStageDefinitions");

            migrationBuilder.DropColumn(
                name: "WarehouseCodesJson",
                table: "ApprovalDecisions");

            migrationBuilder.Sql("""
                UPDATE "ApprovalRequests"
                SET "FromWarehouse" = left("FromWarehouse", 50),
                    "ToWarehouse" = left("ToWarehouse", 50);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "ToWarehouse",
                table: "ApprovalRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FromWarehouse",
                table: "ApprovalRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(400)",
                oldMaxLength: 400,
                oldNullable: true);
        }
    }
}
