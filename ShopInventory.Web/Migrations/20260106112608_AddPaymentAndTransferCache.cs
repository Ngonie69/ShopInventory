using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAndTransferCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedIncomingPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocEntry = table.Column<int>(type: "integer", nullable: false),
                    DocNum = table.Column<int>(type: "integer", nullable: false),
                    DocDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DocDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DocCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CashSum = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CheckSum = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    TransferSum = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CreditSum = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    DocTotal = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TransferReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TransferDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TransferAccount = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PaymentInvoicesJson = table.Column<string>(type: "text", nullable: true),
                    PaymentChecksJson = table.Column<string>(type: "text", nullable: true),
                    PaymentCreditCardsJson = table.Column<string>(type: "text", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedIncomingPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedInventoryTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocEntry = table.Column<int>(type: "integer", nullable: false),
                    DocNum = table.Column<int>(type: "integer", nullable: false),
                    DocDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FromWarehouse = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToWarehouse = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Comments = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LinesJson = table.Column<string>(type: "text", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedInventoryTransfers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedIncomingPayments_CardCode",
                table: "CachedIncomingPayments",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_CachedIncomingPayments_DocDate",
                table: "CachedIncomingPayments",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_CachedIncomingPayments_DocEntry",
                table: "CachedIncomingPayments",
                column: "DocEntry",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedIncomingPayments_DocNum",
                table: "CachedIncomingPayments",
                column: "DocNum");

            migrationBuilder.CreateIndex(
                name: "IX_CachedIncomingPayments_LastSyncedAt",
                table: "CachedIncomingPayments",
                column: "LastSyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CachedInventoryTransfers_DocDate",
                table: "CachedInventoryTransfers",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_CachedInventoryTransfers_DocEntry",
                table: "CachedInventoryTransfers",
                column: "DocEntry",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedInventoryTransfers_DocNum",
                table: "CachedInventoryTransfers",
                column: "DocNum");

            migrationBuilder.CreateIndex(
                name: "IX_CachedInventoryTransfers_FromWarehouse",
                table: "CachedInventoryTransfers",
                column: "FromWarehouse");

            migrationBuilder.CreateIndex(
                name: "IX_CachedInventoryTransfers_LastSyncedAt",
                table: "CachedInventoryTransfers",
                column: "LastSyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CachedInventoryTransfers_ToWarehouse",
                table: "CachedInventoryTransfers",
                column: "ToWarehouse");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedIncomingPayments");

            migrationBuilder.DropTable(
                name: "CachedInventoryTransfers");
        }
    }
}
