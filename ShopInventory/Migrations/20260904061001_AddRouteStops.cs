using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <summary>
    /// The published van sales schedule as data: which areas each route works, and when.
    /// </summary>
    /// <remarks>
    /// One new table plus one nullable column on <c>Routes</c>, so this is safe to run against a live
    /// database mid-deploy. <c>Routes</c> could already name a round and its truck, but nothing
    /// anywhere could say where either was supposed to be — the day-by-day plan lived on paper, and no
    /// report could ask whether a van worked the areas it was sent to.
    ///
    /// The rows themselves are not inserted here. <c>DbInitializer.SeedVanSalesRoutesAsync</c> loads
    /// them from <c>VanSalesRouteSeedData</c> on start, insert-only, so the schedule stays one
    /// editable list rather than a fact frozen into a migration.
    ///
    /// <c>SeedKey</c> on both tables is what lets the office edit a seeded row at all. It records what
    /// the seeder <em>placed</em>, not what the row now says, so a renamed or rescheduled stop is
    /// still recognised as already-seeded on the next start. Recognising rows by their contents
    /// instead means any edit hides the row and the seeder adds the original straight back — on every
    /// deploy, and silently.
    ///
    /// Both its indexes are unique over a nullable column, which is deliberate: rows nobody seeded
    /// hold NULL, PostgreSQL counts NULLs as distinct, and so they never collide with each other while
    /// no seeded row can be placed twice.
    /// </remarks>
    public partial class AddRouteStops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeedKey",
                table: "Routes",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RouteStops",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RouteId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    WeekNumber = table.Column<int>(type: "integer", nullable: true),
                    AlternateSet = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SeedKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteStops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteStops_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Routes_SeedKey",
                table: "Routes",
                column: "SeedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteStops_RouteId_DayOfWeek",
                table: "RouteStops",
                columns: new[] { "RouteId", "DayOfWeek" });

            migrationBuilder.CreateIndex(
                name: "IX_RouteStops_RouteId_IsActive",
                table: "RouteStops",
                columns: new[] { "RouteId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_RouteStops_SeedKey",
                table: "RouteStops",
                column: "SeedKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RouteStops");

            migrationBuilder.DropIndex(
                name: "IX_Routes_SeedKey",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "SeedKey",
                table: "Routes");
        }
    }
}
