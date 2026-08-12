using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanRouteDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VanRouteDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TradingDate = table.Column<DateTime>(type: "date", nullable: false),
                    RouteId = table.Column<int>(type: "integer", nullable: true),
                    RouteCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    RouteName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Territory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TruckRegNo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    DepartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DepartedRecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DepartedLatitude = table.Column<double>(type: "double precision", nullable: true),
                    DepartedLongitude = table.Column<double>(type: "double precision", nullable: true),
                    StartingMileage = table.Column<int>(type: "integer", nullable: true),
                    PlannedCustomerCount = table.Column<int>(type: "integer", nullable: false),
                    ReturnedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReturnedRecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReturnedLatitude = table.Column<double>(type: "double precision", nullable: true),
                    ReturnedLongitude = table.Column<double>(type: "double precision", nullable: true),
                    ClosingMileage = table.Column<int>(type: "integer", nullable: true),
                    RtiOut = table.Column<int>(type: "integer", nullable: true),
                    RtiReturned = table.Column<int>(type: "integer", nullable: true),
                    DeclaredCash = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    DeclaredEcocash = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    DeclaredInnbucks = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    DeclaredCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StartClientReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EndClientReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanRouteDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VanRouteDays_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VanRouteDays_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDays_EndClientReference",
                table: "VanRouteDays",
                column: "EndClientReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDays_RouteCode_TradingDate",
                table: "VanRouteDays",
                columns: new[] { "RouteCode", "TradingDate" });

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDays_RouteId",
                table: "VanRouteDays",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDays_StartClientReference",
                table: "VanRouteDays",
                column: "StartClientReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDays_TradingDate",
                table: "VanRouteDays",
                column: "TradingDate");

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDays_UserId_TradingDate",
                table: "VanRouteDays",
                columns: new[] { "UserId", "TradingDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VanRouteDays");
        }
    }
}
