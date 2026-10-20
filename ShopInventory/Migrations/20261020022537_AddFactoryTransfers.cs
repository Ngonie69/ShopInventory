using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FactoryTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DepartmentCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceWarehouseCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HoldingWarehouseCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DestinationWarehouseCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SentByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentByName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OutSapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    OutSapDocNum = table.Column<int>(type: "integer", nullable: true),
                    OutPostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OutAttempts = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    InSapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    InSapDocNum = table.Column<int>(type: "integer", nullable: true),
                    InPostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactoryTransfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FactoryTransferLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferId = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UoMCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    AcceptedQuantity = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BatchesJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactoryTransferLines", x => x.Id);
                    table.CheckConstraint("CK_FactoryTransferLines_AcceptedQuantity_InRange", "\"AcceptedQuantity\" IS NULL OR (\"AcceptedQuantity\" >= 0 AND \"AcceptedQuantity\" <= \"Quantity\")");
                    table.CheckConstraint("CK_FactoryTransferLines_Quantity_Positive", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_FactoryTransferLines_FactoryTransfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "FactoryTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FactoryTransferLines_TransferId",
                table: "FactoryTransferLines",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "IX_FactoryTransfers_ClientRequestId",
                table: "FactoryTransfers",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FactoryTransfers_DestinationWarehouseCode",
                table: "FactoryTransfers",
                column: "DestinationWarehouseCode");

            migrationBuilder.CreateIndex(
                name: "IX_FactoryTransfers_SourceWarehouseCode",
                table: "FactoryTransfers",
                column: "SourceWarehouseCode");

            migrationBuilder.CreateIndex(
                name: "IX_FactoryTransfers_Status_SentAtUtc",
                table: "FactoryTransfers",
                columns: new[] { "Status", "SentAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FactoryTransferLines");

            migrationBuilder.DropTable(
                name: "FactoryTransfers");
        }
    }
}
