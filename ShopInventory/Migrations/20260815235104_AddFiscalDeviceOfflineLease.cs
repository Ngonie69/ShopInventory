using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalDeviceOfflineLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FiscalDeviceOfflineLeases",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    HolderUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HolderLabel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HolderPendingSales = table.Column<int>(type: "integer", nullable: true),
                    HolderLastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FiscalDeviceOfflineLeases", x => x.DeviceId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FiscalDeviceOfflineLeases");
        }
    }
}
