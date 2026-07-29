using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDesktopFiscalTransactionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DesktopFiscalTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientTransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DocNum = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    VerificationCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    QRCode = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DeviceSerialNumber = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DeviceId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    FiscalDay = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReceiptGlobalNo = table.Column<int>(type: "integer", nullable: true),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DocTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    VatSum = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    OriginalInvoiceNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RawRequest = table.Column<string>(type: "text", nullable: true),
                    RawResponse = table.Column<string>(type: "text", nullable: true),
                    SourceSystem = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedByUsername = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesktopFiscalTransactions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DesktopFiscalTransactions_ClientTransactionId",
                table: "DesktopFiscalTransactions",
                column: "ClientTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DesktopFiscalTransactions_Status_DocumentType_TimestampUtc",
                table: "DesktopFiscalTransactions",
                columns: new[] { "Status", "DocumentType", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DesktopFiscalTransactions_TimestampUtc",
                table: "DesktopFiscalTransactions",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DesktopFiscalTransactions");
        }
    }
}
