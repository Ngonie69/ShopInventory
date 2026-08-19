using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalisationPreflightAndDayLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DesktopSales_ReceiptIngestStatus_ReceiptGlobalNo",
                table: "DesktopSales");

            migrationBuilder.AddColumn<int>(
                name: "FiscalDeviceId",
                table: "DesktopSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SapComexReference",
                table: "DesktopSales",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Carry the device across from the string column that used to hold it.
            //
            // FiscalDeviceNumber has two writers with two meanings: the online path stores the serial the
            // platform reported, an offline van upload stores the numeric device id. Only the numeric one
            // is a device id, so only rows that are entirely digits are moved, and the serials are left
            // where they are.
            //
            // Without this every van receipt already waiting to be submitted would group under a null
            // device. The drain walks one device's receipts in signing order and stops that device on a
            // failure; collapsing every van into one null group would make one van's problem stop all of
            // them.
            //
            // Length-bounded because the target is a 32-bit column and the source is free text.
            migrationBuilder.Sql("""
                UPDATE "DesktopSales"
                SET "FiscalDeviceId" = "FiscalDeviceNumber"::integer
                WHERE "FiscalDeviceNumber" ~ '^[0-9]{1,9}$'
                  AND "FiscalDeviceNumber"::integer > 0;
                """);

            // Refuse to create the one-handset-per-device constraint over data that already breaks it.
            //
            // Postgres would refuse anyway, with a message naming an index and no way to tell which
            // handsets are involved. Two users on one device id means their receipts are already forking
            // that device's chain, so this is worth stopping a deployment for — but it has to say who.
            migrationBuilder.Sql("""
                DO $$
                DECLARE offenders text;
                BEGIN
                    SELECT string_agg(detail, '; ') INTO offenders
                    FROM (
                        SELECT "FiscalDeviceId" || ' -> ' || string_agg("Username", ', ') AS detail
                        FROM "Users"
                        WHERE "FiscalDeviceId" IS NOT NULL
                        GROUP BY "FiscalDeviceId"
                        HAVING count(*) > 1
                    ) AS duplicates;

                    IF offenders IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Cannot make Users.FiscalDeviceId unique: these devices have more than one handset (%). A fiscal device is one hash-chained receipt sequence, so two handsets on one id are already signing conflicting receipts. Clear the duplicate assignments, then re-run this migration.',
                            offenders;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "FiscalDayStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    FiscalDayNo = table.Column<int>(type: "integer", nullable: false),
                    OpenedAtLocal = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    MaxDurationHours = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FileGeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FileSubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OfflineFileReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IngestedReceiptCount = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DurationWarningRaised = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalDayStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_FiscalDeviceId",
                table: "Users",
                column: "FiscalDeviceId",
                unique: true,
                filter: "\"FiscalDeviceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_ReceiptIngestStatus_FiscalDeviceId_ReceiptGlob~",
                table: "DesktopSales",
                columns: new[] { "ReceiptIngestStatus", "FiscalDeviceId", "ReceiptGlobalNo" });

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_SapComexReference",
                table: "DesktopSales",
                column: "SapComexReference");

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDayStates_DeviceId_FiscalDayNo",
                table: "FiscalDayStates",
                columns: new[] { "DeviceId", "FiscalDayNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FiscalDayStates_Status",
                table: "FiscalDayStates",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FiscalDayStates");

            migrationBuilder.DropIndex(
                name: "IX_Users_FiscalDeviceId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_DesktopSales_ReceiptIngestStatus_FiscalDeviceId_ReceiptGlob~",
                table: "DesktopSales");

            migrationBuilder.DropIndex(
                name: "IX_DesktopSales_SapComexReference",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "FiscalDeviceId",
                table: "DesktopSales");

            migrationBuilder.DropColumn(
                name: "SapComexReference",
                table: "DesktopSales");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopSales_ReceiptIngestStatus_ReceiptGlobalNo",
                table: "DesktopSales",
                columns: new[] { "ReceiptIngestStatus", "ReceiptGlobalNo" });
        }
    }
}
