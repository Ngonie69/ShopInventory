using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketBreakages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketBreakages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReportedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportedByName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    VanWarehouseCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionRemarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReturnsWarehouseCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SapDocNum = table.Column<int>(type: "integer", nullable: true),
                    TransferredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketBreakages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketBreakageLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BreakageId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReportedQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ConfirmedQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketBreakageLines", x => x.Id);
                    table.CheckConstraint("CK_MarketBreakageLines_ConfirmedQuantity_NonNegative", "\"ConfirmedQuantity\" IS NULL OR \"ConfirmedQuantity\" >= 0");
                    table.CheckConstraint("CK_MarketBreakageLines_ReportedQuantity_Positive", "\"ReportedQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_MarketBreakageLines_MarketBreakages_BreakageId",
                        column: x => x.BreakageId,
                        principalTable: "MarketBreakages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketBreakageLines_BreakageId",
                table: "MarketBreakageLines",
                column: "BreakageId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketBreakages_ClientRequestId",
                table: "MarketBreakages",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketBreakages_ReportedByUserId_CreatedAtUtc",
                table: "MarketBreakages",
                columns: new[] { "ReportedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketBreakages_Status_CreatedAtUtc",
                table: "MarketBreakages",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketBreakages_VanWarehouseCode",
                table: "MarketBreakages",
                column: "VanWarehouseCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketBreakageLines");

            migrationBuilder.DropTable(
                name: "MarketBreakages");
        }
    }
}
