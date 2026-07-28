using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingInventoryTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingInventoryTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FromWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ToWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    CreatedByRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovalRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    TotalQuantity = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Comments = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DocDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SapDocEntry = table.Column<int>(type: "integer", nullable: true),
                    SapDocNum = table.Column<int>(type: "integer", nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingInventoryTransfers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryTransfers_ClientRequestId",
                table: "PendingInventoryTransfers",
                column: "ClientRequestId",
                unique: true,
                filter: "\"ClientRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryTransfers_CreatedByUserId",
                table: "PendingInventoryTransfers",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryTransfers_FromWarehouse",
                table: "PendingInventoryTransfers",
                column: "FromWarehouse");

            migrationBuilder.CreateIndex(
                name: "IX_PendingInventoryTransfers_Status_CreatedAtUtc",
                table: "PendingInventoryTransfers",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingInventoryTransfers");
        }
    }
}
