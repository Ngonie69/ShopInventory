using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IncomingPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SAPDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SAPDocNum = table.Column<int>(type: "integer", nullable: true),
                    DocDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DocDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DocCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CashSum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CashSumFC = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CheckSum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TransferSum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TransferSumFC = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreditSum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DocTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DocTotalFc = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JournalRemarks = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TransferReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TransferDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TransferAccount = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CashAccount = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncedToSAP = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomingPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SAPDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SAPDocNum = table.Column<int>(type: "integer", nullable: true),
                    DocDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FromWarehouse = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToWarehouse = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Comments = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    JournalMemo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncedToSAP = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SAPDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SAPDocNum = table.Column<int>(type: "integer", nullable: true),
                    DocDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DocDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NumAtCard = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Comments = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DocTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DocTotalFc = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatSum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DocCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SalesPersonCode = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncedToSAP = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SyncError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ItemType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ItemsGroupCode = table.Column<int>(type: "integer", nullable: true),
                    BarCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ManageBatchNumbers = table.Column<bool>(type: "boolean", nullable: false),
                    ManageSerialNumbers = table.Column<bool>(type: "boolean", nullable: false),
                    QuantityOnStock = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    QuantityOrderedFromVendors = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    QuantityOrderedByCustomers = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    InventoryUOM = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SalesUnit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PurchaseUnit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DefaultWarehouse = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedFromSAP = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IncomingPaymentChecks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IncomingPaymentId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CheckNumber = table.Column<int>(type: "integer", nullable: false),
                    BankCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Branch = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AccountNum = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CheckSum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomingPaymentChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncomingPaymentChecks_IncomingPayments_IncomingPaymentId",
                        column: x => x.IncomingPaymentId,
                        principalTable: "IncomingPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncomingPaymentCreditCards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IncomingPaymentId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    CreditCard = table.Column<int>(type: "integer", nullable: false),
                    CreditCardNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardValidUntil = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    VoucherNum = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreditSum = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreditCur = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomingPaymentCreditCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncomingPaymentCreditCards_IncomingPayments_IncomingPayment~",
                        column: x => x.IncomingPaymentId,
                        principalTable: "IncomingPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncomingPaymentInvoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IncomingPaymentId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    InvoiceId = table.Column<int>(type: "integer", nullable: true),
                    SAPDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SumApplied = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SumAppliedFC = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    InvoiceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomingPaymentInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncomingPaymentInvoices_IncomingPayments_IncomingPaymentId",
                        column: x => x.IncomingPaymentId,
                        principalTable: "IncomingPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IncomingPaymentInvoices_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransferLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InventoryTransferId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    FromWarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UoMCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransferLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryTransferLines_InventoryTransfers_InventoryTransfer~",
                        column: x => x.InventoryTransferId,
                        principalTable: "InventoryTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryTransferLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    UoMCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    UoMEntry = table.Column<int>(type: "integer", nullable: true),
                    AccountCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ItemPrices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PriceList = table.Column<int>(type: "integer", nullable: false),
                    PriceListName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    UoMCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    BasePriceList = table.Column<int>(type: "integer", nullable: true),
                    Factor = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedFromSAP = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemPrices_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProductBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ManufacturerSerialNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InternalSerialNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ManufacturingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdmissionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Location = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductBatches_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryTransferLineBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InventoryTransferLineId = table.Column<int>(type: "integer", nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryTransferLineBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryTransferLineBatches_InventoryTransferLines_Invento~",
                        column: x => x.InventoryTransferLineId,
                        principalTable: "InventoryTransferLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLineBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceLineId = table.Column<int>(type: "integer", nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLineBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLineBatches_InvoiceLines_InvoiceLineId",
                        column: x => x.InvoiceLineId,
                        principalTable: "InvoiceLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPaymentChecks_IncomingPaymentId",
                table: "IncomingPaymentChecks",
                column: "IncomingPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPaymentCreditCards_IncomingPaymentId",
                table: "IncomingPaymentCreditCards",
                column: "IncomingPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPaymentInvoices_IncomingPaymentId",
                table: "IncomingPaymentInvoices",
                column: "IncomingPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPaymentInvoices_InvoiceId",
                table: "IncomingPaymentInvoices",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPayments_CardCode",
                table: "IncomingPayments",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPayments_DocDate",
                table: "IncomingPayments",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPayments_SAPDocEntry",
                table: "IncomingPayments",
                column: "SAPDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPayments_SAPDocNum",
                table: "IncomingPayments",
                column: "SAPDocNum");

            migrationBuilder.CreateIndex(
                name: "IX_IncomingPayments_Status",
                table: "IncomingPayments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransferLineBatches_InventoryTransferLineId",
                table: "InventoryTransferLineBatches",
                column: "InventoryTransferLineId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransferLines_InventoryTransferId",
                table: "InventoryTransferLines",
                column: "InventoryTransferId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransferLines_ItemCode",
                table: "InventoryTransferLines",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransferLines_ProductId",
                table: "InventoryTransferLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransfers_DocDate",
                table: "InventoryTransfers",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransfers_SAPDocEntry",
                table: "InventoryTransfers",
                column: "SAPDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransfers_SAPDocNum",
                table: "InventoryTransfers",
                column: "SAPDocNum");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransfers_Status",
                table: "InventoryTransfers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLineBatches_InvoiceLineId",
                table: "InvoiceLineBatches",
                column: "InvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_InvoiceId",
                table: "InvoiceLines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_ItemCode",
                table: "InvoiceLines",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_ProductId",
                table: "InvoiceLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CardCode",
                table: "Invoices",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_DocDate",
                table: "Invoices",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_SAPDocEntry",
                table: "Invoices",
                column: "SAPDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_SAPDocNum",
                table: "Invoices",
                column: "SAPDocNum");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Status",
                table: "Invoices",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ItemPrices_ItemCode",
                table: "ItemPrices",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_ItemPrices_ItemCode_PriceList",
                table: "ItemPrices",
                columns: new[] { "ItemCode", "PriceList" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemPrices_PriceList",
                table: "ItemPrices",
                column: "PriceList");

            migrationBuilder.CreateIndex(
                name: "IX_ItemPrices_ProductId",
                table: "ItemPrices",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductBatches_BatchNumber",
                table: "ProductBatches",
                column: "BatchNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ProductBatches_ProductId_BatchNumber",
                table: "ProductBatches",
                columns: new[] { "ProductId", "BatchNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_BarCode",
                table: "Products",
                column: "BarCode");

            migrationBuilder.CreateIndex(
                name: "IX_Products_ItemCode",
                table: "Products",
                column: "ItemCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncomingPaymentChecks");

            migrationBuilder.DropTable(
                name: "IncomingPaymentCreditCards");

            migrationBuilder.DropTable(
                name: "IncomingPaymentInvoices");

            migrationBuilder.DropTable(
                name: "InventoryTransferLineBatches");

            migrationBuilder.DropTable(
                name: "InvoiceLineBatches");

            migrationBuilder.DropTable(
                name: "ItemPrices");

            migrationBuilder.DropTable(
                name: "ProductBatches");

            migrationBuilder.DropTable(
                name: "IncomingPayments");

            migrationBuilder.DropTable(
                name: "InventoryTransferLines");

            migrationBuilder.DropTable(
                name: "InvoiceLines");

            migrationBuilder.DropTable(
                name: "InventoryTransfers");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
