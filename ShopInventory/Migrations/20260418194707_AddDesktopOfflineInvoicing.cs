using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDesktopOfflineInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyStockSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotDate = table.Column<DateTime>(type: "date", nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyStockSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SaleConsolidations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConsolidationDate = table.Column<DateTime>(type: "date", nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SapDocNum = table.Column<int>(type: "integer", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalVat = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SaleCount = table.Column<int>(type: "integer", nullable: false),
                    PaymentSapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    PaymentSapDocNum = table.Column<int>(type: "integer", nullable: true),
                    PaymentPostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaymentStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleConsolidations", x => x.Id);
                    table.CheckConstraint("CK_SaleConsolidations_SaleCount_Positive", "\"SaleCount\" > 0");
                    table.CheckConstraint("CK_SaleConsolidations_TotalAmount_NonNegative", "\"TotalAmount\" >= 0");
                    table.CheckConstraint("CK_SaleConsolidations_TotalVat_NonNegative", "\"TotalVat\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "StockTransferAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotDate = table.Column<DateTime>(type: "date", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AdjustmentQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Direction = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    TransferDocEntry = table.Column<int>(type: "integer", nullable: true),
                    TransferDocNum = table.Column<int>(type: "integer", nullable: true),
                    SourceWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DestinationWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferAdjustments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyStockSnapshotItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OriginalQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AvailableQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyStockSnapshotItems", x => x.Id);
                    table.CheckConstraint("CK_SnapshotItems_AvailableQuantity_NonNegative", "\"AvailableQuantity\" >= 0");
                    table.CheckConstraint("CK_SnapshotItems_OriginalQuantity_NonNegative", "\"OriginalQuantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_DailyStockSnapshotItems_DailyStockSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "DailyStockSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DesktopSales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExternalReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DocDate = table.Column<DateTime>(type: "date", nullable: false),
                    SalesPersonCode = table.Column<int>(type: "integer", nullable: true),
                    NumAtCard = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Comments = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FiscalizationStatus = table.Column<int>(type: "integer", nullable: false),
                    FiscalReceiptNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FiscalDeviceNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FiscalQRCode = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FiscalVerificationCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FiscalVerificationLink = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FiscalDayNo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FiscalError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConsolidationStatus = table.Column<int>(type: "integer", nullable: false),
                    ConsolidationId = table.Column<int>(type: "integer", nullable: true),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PaymentReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AmountPaid = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesktopSales", x => x.Id);
                    table.CheckConstraint("CK_DesktopSales_AmountPaid_NonNegative", "\"AmountPaid\" >= 0");
                    table.CheckConstraint("CK_DesktopSales_TotalAmount_NonNegative", "\"TotalAmount\" >= 0");
                    table.CheckConstraint("CK_DesktopSales_VatAmount_NonNegative", "\"VatAmount\" >= 0");
                    table.ForeignKey(
                        name: "FK_DesktopSales_SaleConsolidations_ConsolidationId",
                        column: x => x.ConsolidationId,
                        principalTable: "SaleConsolidations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DesktopSaleLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SaleId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    UoMCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesktopSaleLines", x => x.Id);
                    table.CheckConstraint("CK_DesktopSaleLines_DiscountPercent_Valid", "\"DiscountPercent\" >= 0 AND \"DiscountPercent\" <= 100");
                    table.CheckConstraint("CK_DesktopSaleLines_LineTotal_NonNegative", "\"LineTotal\" >= 0");
                    table.CheckConstraint("CK_DesktopSaleLines_Quantity_Positive", "\"Quantity\" > 0");
                    table.CheckConstraint("CK_DesktopSaleLines_UnitPrice_NonNegative", "\"UnitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_DesktopSaleLines_DesktopSales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "DesktopSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyStockSnapshotItems_ItemCode_WarehouseCode",
                table: "DailyStockSnapshotItems",
                columns: new[] { "ItemCode", "WarehouseCode" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyStockSnapshotItems_SnapshotId_ItemCode_BatchNumber",
                table: "DailyStockSnapshotItems",
                columns: new[] { "SnapshotId", "ItemCode", "BatchNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyStockSnapshots_SnapshotDate_WarehouseCode",
                table: "DailyStockSnapshots",
                columns: new[] { "SnapshotDate", "WarehouseCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyStockSnapshots_Status",
                table: "DailyStockSnapshots",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSaleLines_ItemCode_WarehouseCode",
                table: "DesktopSaleLines",
                columns: new[] { "ItemCode", "WarehouseCode" });

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSaleLines_SaleId",
                table: "DesktopSaleLines",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_CardCode",
                table: "DesktopSales",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_ConsolidationId",
                table: "DesktopSales",
                column: "ConsolidationId");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_ConsolidationStatus",
                table: "DesktopSales",
                column: "ConsolidationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_DocDate",
                table: "DesktopSales",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_ExternalReferenceId",
                table: "DesktopSales",
                column: "ExternalReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_WarehouseCode",
                table: "DesktopSales",
                column: "WarehouseCode");

            migrationBuilder.CreateIndex(
                name: "IX_SaleConsolidations_CardCode_ConsolidationDate",
                table: "SaleConsolidations",
                columns: new[] { "CardCode", "ConsolidationDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleConsolidations_ConsolidationDate",
                table: "SaleConsolidations",
                column: "ConsolidationDate");

            migrationBuilder.CreateIndex(
                name: "IX_SaleConsolidations_Status",
                table: "SaleConsolidations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferAdjustments_SnapshotDate",
                table: "StockTransferAdjustments",
                column: "SnapshotDate");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferAdjustments_SnapshotDate_ItemCode_WarehouseCod~",
                table: "StockTransferAdjustments",
                columns: new[] { "SnapshotDate", "ItemCode", "WarehouseCode", "TransferDocEntry", "Direction" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyStockSnapshotItems");

            migrationBuilder.DropTable(
                name: "DesktopSaleLines");

            migrationBuilder.DropTable(
                name: "StockTransferAdjustments");

            migrationBuilder.DropTable(
                name: "DailyStockSnapshots");

            migrationBuilder.DropTable(
                name: "DesktopSales");

            migrationBuilder.DropTable(
                name: "SaleConsolidations");
        }
    }
}
