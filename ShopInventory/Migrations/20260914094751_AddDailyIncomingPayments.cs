using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyIncomingPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyIncomingPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentDate = table.Column<DateTime>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CashSum = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TransferSum = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreditSum = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PostIssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SapDocNum = table.Column<int>(type: "integer", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyIncomingPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyIncomingPaymentLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DailyIncomingPaymentId = table.Column<int>(type: "integer", nullable: false),
                    DesktopSaleId = table.Column<int>(type: "integer", nullable: true),
                    SaleConsolidationId = table.Column<int>(type: "integer", nullable: true),
                    InvoiceDocEntry = table.Column<int>(type: "integer", nullable: false),
                    InvoiceDocNum = table.Column<int>(type: "integer", nullable: true),
                    CashAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TransferAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreditAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyIncomingPaymentLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyIncomingPaymentLines_DailyIncomingPayments_DailyIncomi~",
                        column: x => x.DailyIncomingPaymentId,
                        principalTable: "DailyIncomingPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyIncomingPaymentLines_DailyIncomingPaymentId",
                table: "DailyIncomingPaymentLines",
                column: "DailyIncomingPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyIncomingPaymentLines_DesktopSaleId",
                table: "DailyIncomingPaymentLines",
                column: "DesktopSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyIncomingPaymentLines_InvoiceDocEntry",
                table: "DailyIncomingPaymentLines",
                column: "InvoiceDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_DailyIncomingPaymentLines_SaleConsolidationId",
                table: "DailyIncomingPaymentLines",
                column: "SaleConsolidationId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyIncomingPayments_CardCode_PaymentDate",
                table: "DailyIncomingPayments",
                columns: new[] { "CardCode", "PaymentDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyIncomingPayments_Status",
                table: "DailyIncomingPayments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyIncomingPaymentLines");

            migrationBuilder.DropTable(
                name: "DailyIncomingPayments");
        }
    }
}
