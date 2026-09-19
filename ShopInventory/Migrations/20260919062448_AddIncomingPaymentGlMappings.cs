using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomingPaymentGlMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CashAccount",
                table: "DailyIncomingPayments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailError",
                table: "DailyIncomingPayments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailSentAtUtc",
                table: "DailyIncomingPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransferAccount",
                table: "DailyIncomingPayments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PayOpenBalance",
                table: "DailyIncomingPaymentLines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "StockReservationId",
                table: "DailyIncomingPaymentLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IncomingPaymentGlMappings",
                columns: table => new
                {
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CashAccount = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ElectronicAccount = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Run = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    NotifyEmails = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomingPaymentGlMappings", x => x.CardCode);
                });

            migrationBuilder.InsertData(
                table: "IncomingPaymentGlMappings",
                columns: new[] { "CardCode", "CardName", "CashAccount", "ElectronicAccount", "IsActive", "NeedsReview", "NotifyEmails", "Run", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[,]
                {
                    { "CIS006", "Factory Kefalos shop POS  USD", "700300", "701100", true, false, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "COR006", "Cortina Graniteside Vending", "700610", "701100", true, true, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "COR007", "Graniteside Kefalos Shop USD POS", "700610", "701100", true, false, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "COR008", "Cortina Bulawayo Vending", "701500", "701100", true, true, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "COR011", "Bulawayo Kefalos Shop USD POS", "701500", "701100", true, false, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "MAC006", "Cortina Machipisa Vending", "701500", "701100", true, true, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "MAC009", "Machipisa Kefalos Shop USD POS", "701500", "701100", true, false, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "MAC011", "Cortina Vending Special Events", "701500", "701100", true, true, null, "Shops", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN008", "Van Sales West 2", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN009", "Van Sales West 1", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN010", "Van Sales CBD", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN013", "Van Sales East", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN014", "Van Sales Up Country 1", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN015", "Van Sales Up Country 2", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN016", "Van Sales Up Country 3", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN017", "Van Sales Up Country 4", "700610", "700610", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN018", "Van Sales Bulawayo - Local", "701500", "701500", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" },
                    { "VAN019", "Van Sales Up Country 2 - Bulawayo", "701500", "701500", true, false, null, "Vans", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "seed" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyIncomingPaymentLines_StockReservationId",
                table: "DailyIncomingPaymentLines",
                column: "StockReservationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncomingPaymentGlMappings");

            migrationBuilder.DropIndex(
                name: "IX_DailyIncomingPaymentLines_StockReservationId",
                table: "DailyIncomingPaymentLines");

            migrationBuilder.DropColumn(
                name: "CashAccount",
                table: "DailyIncomingPayments");

            migrationBuilder.DropColumn(
                name: "EmailError",
                table: "DailyIncomingPayments");

            migrationBuilder.DropColumn(
                name: "EmailSentAtUtc",
                table: "DailyIncomingPayments");

            migrationBuilder.DropColumn(
                name: "TransferAccount",
                table: "DailyIncomingPayments");

            migrationBuilder.DropColumn(
                name: "PayOpenBalance",
                table: "DailyIncomingPaymentLines");

            migrationBuilder.DropColumn(
                name: "StockReservationId",
                table: "DailyIncomingPaymentLines");
        }
    }
}
