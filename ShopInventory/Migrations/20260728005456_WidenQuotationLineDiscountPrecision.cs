using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class WidenQuotationLineDiscountPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "DiscountPercent",
                table: "QuotationLines",
                type: "numeric(9,6)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(5,2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "DiscountPercent",
                table: "QuotationLines",
                type: "numeric(5,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)");
        }
    }
}
