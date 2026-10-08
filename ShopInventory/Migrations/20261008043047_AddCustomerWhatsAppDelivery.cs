using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerWhatsAppDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerWhatsAppContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RouteCustomerId = table.Column<int>(type: "integer", nullable: true),
                    OwnerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PhoneE164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContactName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AutoSendInvoices = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentSource = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConsentNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConsentRecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsentRecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConsentRecordedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OptedOutAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OptedOutSource = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    OptedOutBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WhatsAppExists = table.Column<bool>(type: "boolean", nullable: true),
                    WhatsAppCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemovedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerWhatsAppContacts", x => x.Id);
                    table.CheckConstraint("CK_CustomerWhatsAppContacts_OneOwner", "(\"CardCode\" IS NULL) <> (\"RouteCustomerId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CustomerWhatsAppContacts_RouteCustomers_RouteCustomerId",
                        column: x => x.RouteCustomerId,
                        principalTable: "RouteCustomers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerDocumentDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SapDocNum = table.Column<int>(type: "integer", nullable: true),
                    DesktopSaleId = table.Column<int>(type: "integer", nullable: true),
                    DocumentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SaleReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DocumentDate = table.Column<DateTime>(type: "date", nullable: true),
                    DocumentTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    DocumentTotalFc = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RouteCustomerId = table.Column<int>(type: "integer", nullable: true),
                    RouteCustomerCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RouteCustomerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContactId = table.Column<int>(type: "integer", nullable: true),
                    RecipientE164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecipientCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConsentAffirmed = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StatusReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DispatchAttempts = table.Column<int>(type: "integer", nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SendIssuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GatewayTimestamp = table.Column<long>(type: "bigint", nullable: true),
                    SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FileSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FileBytes = table.Column<int>(type: "integer", nullable: true),
                    Caption = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FiscalQrCode = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FiscalVerificationCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FiscalEvidenceSource = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SupersedesDeliveryId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDocumentDeliveries", x => x.Id);
                    table.CheckConstraint("CK_CustomerDocumentDeliveries_DispatchAttempts_NonNegative", "\"DispatchAttempts\" >= 0");
                    table.CheckConstraint("CK_CustomerDocumentDeliveries_OneDocument", "(\"SapDocEntry\" IS NULL) <> (\"DesktopSaleId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CustomerDocumentDeliveries_CustomerWhatsAppContacts_Contact~",
                        column: x => x.ContactId,
                        principalTable: "CustomerWhatsAppContacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerDocumentDeliveries_DesktopSales_DesktopSaleId",
                        column: x => x.DesktopSaleId,
                        principalTable: "DesktopSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerDocumentDeliveries_RouteCustomers_RouteCustomerId",
                        column: x => x.RouteCustomerId,
                        principalTable: "RouteCustomers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_CardCode_CreatedAtUtc",
                table: "CustomerDocumentDeliveries",
                columns: new[] { "CardCode", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_ContactId",
                table: "CustomerDocumentDeliveries",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_DesktopSaleId_RecipientE164",
                table: "CustomerDocumentDeliveries",
                columns: new[] { "DesktopSaleId", "RecipientE164" },
                unique: true,
                filter: "\"Trigger\" = 'Counter' AND \"DesktopSaleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_RecipientE164_CreatedAtUtc",
                table: "CustomerDocumentDeliveries",
                columns: new[] { "RecipientE164", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_RouteCustomerId_CreatedAtUtc",
                table: "CustomerDocumentDeliveries",
                columns: new[] { "RouteCustomerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_SapDocEntry",
                table: "CustomerDocumentDeliveries",
                column: "SapDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_SapDocEntry_RecipientE164",
                table: "CustomerDocumentDeliveries",
                columns: new[] { "SapDocEntry", "RecipientE164" },
                unique: true,
                filter: "\"Trigger\" = 'Auto' AND \"SapDocEntry\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_SendIssuedAtUtc",
                table: "CustomerDocumentDeliveries",
                column: "SendIssuedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDocumentDeliveries_Status_NextAttemptAtUtc",
                table: "CustomerDocumentDeliveries",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerWhatsAppContacts_CardCode_PhoneE164",
                table: "CustomerWhatsAppContacts",
                columns: new[] { "CardCode", "PhoneE164" },
                unique: true,
                filter: "\"CardCode\" IS NOT NULL AND \"RemovedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerWhatsAppContacts_PhoneE164",
                table: "CustomerWhatsAppContacts",
                column: "PhoneE164");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerWhatsAppContacts_RouteCustomerId_PhoneE164",
                table: "CustomerWhatsAppContacts",
                columns: new[] { "RouteCustomerId", "PhoneE164" },
                unique: true,
                filter: "\"RouteCustomerId\" IS NOT NULL AND \"RemovedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerDocumentDeliveries");

            migrationBuilder.DropTable(
                name: "CustomerWhatsAppContacts");
        }
    }
}
