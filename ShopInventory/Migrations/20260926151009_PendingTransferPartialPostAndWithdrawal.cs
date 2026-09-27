using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class PendingTransferPartialPostAndWithdrawal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DroppedLinesJson",
                table: "PendingInventoryTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PostRecordedManually",
                table: "PendingInventoryTransfers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WithdrawalReason",
                table: "PendingInventoryTransfers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WithdrawnAtUtc",
                table: "PendingInventoryTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WithdrawnByUserId",
                table: "PendingInventoryTransfers",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DroppedLinesJson",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "PostRecordedManually",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "WithdrawalReason",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "WithdrawnAtUtc",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "WithdrawnByUserId",
                table: "PendingInventoryTransfers");
        }
    }
}
