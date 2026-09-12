using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDesktopCreditNoteSapPosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SapAttempts",
                table: "DesktopCreditNotes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SapError",
                table: "DesktopCreditNotes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SapPostIssuedAtUtc",
                table: "DesktopCreditNotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SapPostedAt",
                table: "DesktopCreditNotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SapReference",
                table: "DesktopCreditNotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // "Deferred", not the generated "". Credits raised before this deploy are fiscalised and
            // owe SAP a document, and an empty status matches no DesktopCreditSapStatuses value — so
            // the sweep would never select them and they would be stranded, silently, forever.
            migrationBuilder.AddColumn<string>(
                name: "SapStatus",
                table: "DesktopCreditNotes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Deferred");

            migrationBuilder.AddColumn<bool>(
                name: "UnitsReturnedToLedger",
                table: "DesktopCreditNotes",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SapAttempts",
                table: "DesktopCreditNotes");

            migrationBuilder.DropColumn(
                name: "SapError",
                table: "DesktopCreditNotes");

            migrationBuilder.DropColumn(
                name: "SapPostIssuedAtUtc",
                table: "DesktopCreditNotes");

            migrationBuilder.DropColumn(
                name: "SapPostedAt",
                table: "DesktopCreditNotes");

            migrationBuilder.DropColumn(
                name: "SapReference",
                table: "DesktopCreditNotes");

            migrationBuilder.DropColumn(
                name: "SapStatus",
                table: "DesktopCreditNotes");

            migrationBuilder.DropColumn(
                name: "UnitsReturnedToLedger",
                table: "DesktopCreditNotes");
        }
    }
}
