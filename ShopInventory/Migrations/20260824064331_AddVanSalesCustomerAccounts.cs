using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanSalesCustomerAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VanSalesCustomerAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RouteCustomerId = table.Column<int>(type: "integer", nullable: false),
                    PhoneE164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    FailedOtpCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanSalesCustomerAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VanSalesCustomerAccounts_RouteCustomers_RouteCustomerId",
                        column: x => x.RouteCustomerId,
                        principalTable: "RouteCustomers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VanSalesCustomerAccounts_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "VanSalesCustomerOtps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PhoneE164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    RequestedFromIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeliveryChannel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanSalesCustomerOtps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VanSalesCustomerRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VanSalesCustomerAccountId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedByIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanSalesCustomerRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VanSalesCustomerRefreshTokens_VanSalesCustomerAccounts_VanS~",
                        column: x => x.VanSalesCustomerAccountId,
                        principalTable: "VanSalesCustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerAccounts_CreatedByUserId",
                table: "VanSalesCustomerAccounts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerAccounts_PhoneE164",
                table: "VanSalesCustomerAccounts",
                column: "PhoneE164",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerAccounts_RouteCustomerId",
                table: "VanSalesCustomerAccounts",
                column: "RouteCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerOtps_PhoneE164_ExpiresAt",
                table: "VanSalesCustomerOtps",
                columns: new[] { "PhoneE164", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerRefreshTokens_TokenHash",
                table: "VanSalesCustomerRefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesCustomerRefreshTokens_VanSalesCustomerAccountId",
                table: "VanSalesCustomerRefreshTokens",
                column: "VanSalesCustomerAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VanSalesCustomerOtps");

            migrationBuilder.DropTable(
                name: "VanSalesCustomerRefreshTokens");

            migrationBuilder.DropTable(
                name: "VanSalesCustomerAccounts");
        }
    }
}
