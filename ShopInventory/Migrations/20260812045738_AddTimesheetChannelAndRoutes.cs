using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddTimesheetChannelAndRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RouteId",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "TimesheetEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CheckInClientReference",
                table: "TimesheetEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CheckInLocationAccuracyMetres",
                table: "TimesheetEntries",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckInLocationSource",
                table: "TimesheetEntries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckInRecordedAt",
                table: "TimesheetEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckOutClientReference",
                table: "TimesheetEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CheckOutLocationAccuracyMetres",
                table: "TimesheetEntries",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckOutLocationSource",
                table: "TimesheetEntries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckOutRecordedAt",
                table: "TimesheetEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationUnavailableReason",
                table: "TimesheetEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Territory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TruckRegNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Routes_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_RouteId",
                table: "Users",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetEntries_Channel_CheckInTime",
                table: "TimesheetEntries",
                columns: new[] { "Channel", "CheckInTime" });

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetEntries_CheckInClientReference",
                table: "TimesheetEntries",
                column: "CheckInClientReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimesheetEntries_CheckOutClientReference",
                table: "TimesheetEntries",
                column: "CheckOutClientReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_Code",
                table: "Routes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_CreatedByUserId",
                table: "Routes",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Routes_IsActive",
                table: "Routes",
                column: "IsActive");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Routes_RouteId",
                table: "Users",
                column: "RouteId",
                principalTable: "Routes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Label the history.
            //
            // Every existing row takes the column default, Merchandiser (0), which is wrong for every
            // visit a van rep ever made — and they are in here, because the handset's
            // /api/vansales/attendance route has always written to this same table. Left unlabelled,
            // the merchandiser page would show years of van visits and the van page would show none.
            //
            // Role is the only evidence available after the fact, and it is the same test the rest of
            // the system already uses to decide whether an account behaves as a van:
            // ApplicationRoles.LegacyVanSalesRoles, read by VanSalesRouteCustomerScope.
            //
            // It is evidence and not proof — an account that has since changed role is labelled by
            // what it is now, not by what it was. That is a one-off cost on existing rows only:
            // everything written from here on is stamped at check-in by the handler.
            migrationBuilder.Sql("""
                UPDATE "TimesheetEntries" AS t
                SET "Channel" = 1
                FROM "Users" AS u
                WHERE u."Id" = t."UserId"
                  AND upper(u."Role") IN ('ADR', 'SALES');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Routes_RouteId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropIndex(
                name: "IX_Users_RouteId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_TimesheetEntries_Channel_CheckInTime",
                table: "TimesheetEntries");

            migrationBuilder.DropIndex(
                name: "IX_TimesheetEntries_CheckInClientReference",
                table: "TimesheetEntries");

            migrationBuilder.DropIndex(
                name: "IX_TimesheetEntries_CheckOutClientReference",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "RouteId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckInClientReference",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckInLocationAccuracyMetres",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckInLocationSource",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckInRecordedAt",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckOutClientReference",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckOutLocationAccuracyMetres",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckOutLocationSource",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "CheckOutRecordedAt",
                table: "TimesheetEntries");

            migrationBuilder.DropColumn(
                name: "LocationUnavailableReason",
                table: "TimesheetEntries");
        }
    }
}
