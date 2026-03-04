using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerLinkedAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountStructure",
                table: "CustomerPortalUsers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Single");

            migrationBuilder.CreateTable(
                name: "CustomerLinkedAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerPortalUserId = table.Column<int>(type: "integer", nullable: false),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Main"),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ParentCardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedTransactions = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerLinkedAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerLinkedAccounts_CustomerPortalUsers_CustomerPortalUs~",
                        column: x => x.CustomerPortalUserId,
                        principalTable: "CustomerPortalUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLinkedAccounts_AccountType",
                table: "CustomerLinkedAccounts",
                column: "AccountType");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLinkedAccounts_CardCode",
                table: "CustomerLinkedAccounts",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLinkedAccounts_CustomerPortalUserId",
                table: "CustomerLinkedAccounts",
                column: "CustomerPortalUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLinkedAccounts_CustomerPortalUserId_CardCode",
                table: "CustomerLinkedAccounts",
                columns: new[] { "CustomerPortalUserId", "CardCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerLinkedAccounts_ParentCardCode",
                table: "CustomerLinkedAccounts",
                column: "ParentCardCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerLinkedAccounts");

            migrationBuilder.DropColumn(
                name: "AccountStructure",
                table: "CustomerPortalUsers");
        }
    }
}
