using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingTransferRequestEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingTransferRequestEdits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestDocEntry = table.Column<int>(type: "integer", nullable: false),
                    RequestDocNum = table.Column<int>(type: "integer", nullable: false),
                    FromWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ToWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProposedLinesJson = table.Column<string>(type: "text", nullable: false),
                    OriginalLinesJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    CreatedByRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTransferRequestEdits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransferRequestEdits_CreatedByUserId",
                table: "PendingTransferRequestEdits",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransferRequestEdits_RequestDocEntry",
                table: "PendingTransferRequestEdits",
                column: "RequestDocEntry");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransferRequestEdits_Status_CreatedAtUtc",
                table: "PendingTransferRequestEdits",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingTransferRequestEdits");
        }
    }
}
