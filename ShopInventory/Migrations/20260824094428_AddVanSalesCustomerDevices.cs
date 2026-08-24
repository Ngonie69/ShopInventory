using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanSalesCustomerDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VanSalesCustomerDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VanSalesCustomerAccountId = table.Column<int>(type: "integer", nullable: false),
                    DeviceToken = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActiveAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanSalesCustomerDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VanSalesCustomerDevices_VanSalesCustomerAccounts_VanSalesCu~",
                        column: x => x.VanSalesCustomerAccountId,
                        principalTable: "VanSalesCustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerDevices_DeviceToken",
                table: "VanSalesCustomerDevices",
                column: "DeviceToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerDevices_VanSalesCustomerAccountId",
                table: "VanSalesCustomerDevices",
                column: "VanSalesCustomerAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VanSalesCustomerDevices");
        }
    }
}
