using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// Adds the day each partner's daily payment pays from. Switching a partner on sets it, so invoices its
    /// cashier already settled, often as a receipt on account that SAP's balances cannot show, are never
    /// paid a second time.
    /// </summary>
    public partial class AddDailyPaymentPaysFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PaysFrom",
                table: "IncomingPaymentGlMappings",
                type: "date",
                nullable: true);

            // A partner already switched on has been paid daily since its last change, so nothing earlier is
            // waiting on it. Floored there, so a backlog the cashier settled on account is never claimed.
            migrationBuilder.Sql("""
                UPDATE "IncomingPaymentGlMappings"
                SET "PaysFrom" = ("UpdatedAtUtc" AT TIME ZONE 'Africa/Harare')::date
                WHERE "IsActive";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaysFrom",
                table: "IncomingPaymentGlMappings");
        }
    }
}
