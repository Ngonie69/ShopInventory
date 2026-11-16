using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddFactoryTransferType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SapApprovalState",
                table: "PendingInventoryTransfers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SapApprovalTemplates",
                table: "PendingInventoryTransfers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SapCheckedAtUtc",
                table: "PendingInventoryTransfers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SapDraftDocEntry",
                table: "PendingInventoryTransfers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SapDraftDocNum",
                table: "PendingInventoryTransfers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransferType",
                table: "PendingInventoryTransfers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SapApprovalState",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "SapApprovalTemplates",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "SapCheckedAtUtc",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "SapDraftDocEntry",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "SapDraftDocNum",
                table: "PendingInventoryTransfers");

            migrationBuilder.DropColumn(
                name: "TransferType",
                table: "PendingInventoryTransfers");
        }
    }
}
