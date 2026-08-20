using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteAssignmentOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RouteAssignmentOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RouteName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsRemoval = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteAssignmentOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RouteAssignmentOverrides_CardCode_RouteName",
                table: "RouteAssignmentOverrides",
                columns: new[] { "CardCode", "RouteName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteAssignmentOverrides_RouteName",
                table: "RouteAssignmentOverrides",
                column: "RouteName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RouteAssignmentOverrides");
        }
    }
}
