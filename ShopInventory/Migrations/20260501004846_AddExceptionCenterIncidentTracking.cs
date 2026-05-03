using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddExceptionCenterIncidentTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExceptionCenterIncidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRetryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CanRetry = table.Column<bool>(type: "boolean", nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExceptionCenterIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExceptionCenterItemStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false),
                    IsAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgedByUsername = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedToUsername = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExceptionCenterItemStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterIncidents_Category_Status",
                table: "ExceptionCenterIncidents",
                columns: new[] { "Category", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterIncidents_CreatedAtUtc",
                table: "ExceptionCenterIncidents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterIncidents_Provider",
                table: "ExceptionCenterIncidents",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterIncidents_Source",
                table: "ExceptionCenterIncidents",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterIncidents_Source_OccurredAtUtc",
                table: "ExceptionCenterIncidents",
                columns: new[] { "Source", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterIncidents_Status",
                table: "ExceptionCenterIncidents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterItemStates_AssignedToUsername",
                table: "ExceptionCenterItemStates",
                column: "AssignedToUsername");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionCenterItemStates_Source_ItemId",
                table: "ExceptionCenterItemStates",
                columns: new[] { "Source", "ItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExceptionCenterIncidents");

            migrationBuilder.DropTable(
                name: "ExceptionCenterItemStates");
        }
    }
}
