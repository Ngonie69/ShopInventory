using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeRateMonitor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExchangeRateMonitorAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateMonitorAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRateMonitorStates",
                columns: table => new
                {
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    KeyedThrough = table.Column<DateOnly>(type: "date", nullable: true),
                    KeyedBeyondWindow = table.Column<bool>(type: "boolean", nullable: false),
                    LocalCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    IndirectQuotation = table.Column<bool>(type: "boolean", nullable: true),
                    PriceListFactorsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateMonitorStates", x => x.Currency);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeRateObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    IndirectQuotation = table.Column<bool>(type: "boolean", nullable: false),
                    LocalCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ReplacedRate = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SupersededAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRateObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateMonitorAlerts_AlertKey",
                table: "ExchangeRateMonitorAlerts",
                column: "AlertKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateMonitorAlerts_Currency_RaisedAtUtc",
                table: "ExchangeRateMonitorAlerts",
                columns: new[] { "Currency", "RaisedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRateObservations_Currency_RateDate_Current",
                table: "ExchangeRateObservations",
                columns: new[] { "Currency", "RateDate" },
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeRateMonitorAlerts");

            migrationBuilder.DropTable(
                name: "ExchangeRateMonitorStates");

            migrationBuilder.DropTable(
                name: "ExchangeRateObservations");
        }
    }
}
