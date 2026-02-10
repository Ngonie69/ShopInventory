using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddStockReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservationId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TotalValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SAPDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SAPDocNum = table.Column<int>(type: "integer", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastRenewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenewalCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservations", x => x.Id);
                    table.CheckConstraint("CK_StockReservations_TotalValue_NonNegative", "\"TotalValue\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "StockReservationLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservationId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReservedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    OriginalQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UoMCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DiscountPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservationLines", x => x.Id);
                    table.CheckConstraint("CK_StockReservationLines_LineTotal_NonNegative", "\"LineTotal\" >= 0");
                    table.CheckConstraint("CK_StockReservationLines_ReservedQuantity_Positive", "\"ReservedQuantity\" > 0");
                    table.CheckConstraint("CK_StockReservationLines_UnitPrice_NonNegative", "\"UnitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockReservationLines_StockReservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "StockReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockReservationBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservationLineId = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BatchNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReservedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservationBatches", x => x.Id);
                    table.CheckConstraint("CK_StockReservationBatches_ReservedQuantity_Positive", "\"ReservedQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_StockReservationBatches_StockReservationLines_ReservationLi~",
                        column: x => x.ReservationLineId,
                        principalTable: "StockReservationLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservationBatches_ItemCode_WarehouseCode_BatchNumber",
                table: "StockReservationBatches",
                columns: new[] { "ItemCode", "WarehouseCode", "BatchNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservationBatches_ReservationLineId",
                table: "StockReservationBatches",
                column: "ReservationLineId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservationLines_ItemCode_WarehouseCode",
                table: "StockReservationLines",
                columns: new[] { "ItemCode", "WarehouseCode" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservationLines_ReservationId",
                table: "StockReservationLines",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_CardCode",
                table: "StockReservations",
                column: "CardCode");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_ExpiresAt",
                table: "StockReservations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_ExternalReferenceId",
                table: "StockReservations",
                column: "ExternalReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_ReservationId",
                table: "StockReservations",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_SourceSystem",
                table: "StockReservations",
                column: "SourceSystem");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_Status",
                table: "StockReservations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_Status_ExpiresAt",
                table: "StockReservations",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockReservationBatches");

            migrationBuilder.DropTable(
                name: "StockReservationLines");

            migrationBuilder.DropTable(
                name: "StockReservations");
        }
    }
}
