using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundWorkerClusterStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundWorkerClusterStates",
                columns: table => new
                {
                    WorkerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false),
                    HealthyWindowSeconds = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSuccessfulRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    MachineName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProcessId = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundWorkerClusterStates", x => new { x.WorkerName, x.InstanceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundWorkerClusterStates_LastHeartbeatUtc",
                table: "BackgroundWorkerClusterStates",
                column: "LastHeartbeatUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundWorkerClusterStates_WorkerName_Mode_LastHeartbeat~",
                table: "BackgroundWorkerClusterStates",
                columns: new[] { "WorkerName", "Mode", "LastHeartbeatUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundWorkerClusterStates");
        }
    }
}
