using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerPortalEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerPortalUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PasswordSalt = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorSecret = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    EmailVerificationToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordResetToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordResetTokenExpiry = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedLoginAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastLoginIp = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastPasswordChangeAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PasswordExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                    PreviousPasswordHashes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Active")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPortalUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRateLimits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IdentifierType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "IP"),
                    Endpoint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestCount = table.Column<int>(type: "integer", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    BlockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BlockCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRateLimits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByIp = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RevokedByIp = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReplacedByToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeviceFingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerSecurityLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    GeoLocation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RiskScore = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerSecurityLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPortalUsers_CardCode",
                table: "CustomerPortalUsers",
                column: "CardCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPortalUsers_Email",
                table: "CustomerPortalUsers",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPortalUsers_Status",
                table: "CustomerPortalUsers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRateLimits_Identifier_Endpoint",
                table: "CustomerRateLimits",
                columns: new[] { "Identifier", "Endpoint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRateLimits_WindowEnd",
                table: "CustomerRateLimits",
                column: "WindowEnd");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefreshTokens_CardCode",
                table: "CustomerRefreshTokens",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefreshTokens_ExpiresAt",
                table: "CustomerRefreshTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefreshTokens_TokenHash",
                table: "CustomerRefreshTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSecurityLogs_Action",
                table: "CustomerSecurityLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSecurityLogs_CardCode",
                table: "CustomerSecurityLogs",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSecurityLogs_CardCode_Timestamp",
                table: "CustomerSecurityLogs",
                columns: new[] { "CardCode", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSecurityLogs_Timestamp",
                table: "CustomerSecurityLogs",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerPortalUsers");

            migrationBuilder.DropTable(
                name: "CustomerRateLimits");

            migrationBuilder.DropTable(
                name: "CustomerRefreshTokens");

            migrationBuilder.DropTable(
                name: "CustomerSecurityLogs");
        }
    }
}
