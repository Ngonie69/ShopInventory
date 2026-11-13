using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVendingCashUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VendingCashUps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CashierUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CashierUsername = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BusinessPartnerCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WarehouseCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BusinessDate = table.Column<DateTime>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VarianceReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    BalancedOnFirstCount = table.Column<bool>(type: "boolean", nullable: false),
                    CapturedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CapturedByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SavedByUserIds = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QueryNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    QueriedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    QueriedByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    QueriedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendingCashUps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendingCashUpCurrencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CashUpId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ExpectedCash = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ExpectedCashSales = table.Column<int>(type: "integer", nullable: false),
                    CountedCash = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Banked = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    DepositReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendingCashUpCurrencies", x => x.Id);
                    table.CheckConstraint("CK_VendingCashUpCurrencies_NonNegative", "\"OpeningFloat\" >= 0 AND \"CountedCash\" >= 0 AND \"Banked\" >= 0");
                    table.ForeignKey(
                        name: "FK_VendingCashUpCurrencies_VendingCashUps_CashUpId",
                        column: x => x.CashUpId,
                        principalTable: "VendingCashUps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendingCashUpDenominations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CashUpId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendingCashUpDenominations", x => x.Id);
                    table.CheckConstraint("CK_VendingCashUpDenominations_Positive", "\"Value\" > 0 AND \"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_VendingCashUpDenominations_VendingCashUps_CashUpId",
                        column: x => x.CashUpId,
                        principalTable: "VendingCashUps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendingCashUpTenders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CashUpId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Tender = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Expected = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Declared = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendingCashUpTenders", x => x.Id);
                    table.CheckConstraint("CK_VendingCashUpTenders_NonNegative", "\"Declared\" >= 0");
                    table.ForeignKey(
                        name: "FK_VendingCashUpTenders_VendingCashUps_CashUpId",
                        column: x => x.CashUpId,
                        principalTable: "VendingCashUps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendingCashUpCurrencies_CashUpId_Currency",
                table: "VendingCashUpCurrencies",
                columns: new[] { "CashUpId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendingCashUpDenominations_CashUpId_Currency_Value",
                table: "VendingCashUpDenominations",
                columns: new[] { "CashUpId", "Currency", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendingCashUps_BusinessDate_Status",
                table: "VendingCashUps",
                columns: new[] { "BusinessDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VendingCashUps_CashierUserId_WarehouseCode_BusinessDate",
                table: "VendingCashUps",
                columns: new[] { "CashierUserId", "WarehouseCode", "BusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendingCashUpTenders_CashUpId_Currency_Tender",
                table: "VendingCashUpTenders",
                columns: new[] { "CashUpId", "Currency", "Tender" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendingCashUpCurrencies");

            migrationBuilder.DropTable(
                name: "VendingCashUpDenominations");

            migrationBuilder.DropTable(
                name: "VendingCashUpTenders");

            migrationBuilder.DropTable(
                name: "VendingCashUps");
        }
    }
}
