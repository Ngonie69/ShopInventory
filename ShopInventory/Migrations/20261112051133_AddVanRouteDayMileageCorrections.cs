using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanRouteDayMileageCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VanRouteDayMileageCorrections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VanRouteDayId = table.Column<int>(type: "integer", nullable: false),
                    PreviousStartingMileage = table.Column<int>(type: "integer", nullable: true),
                    PreviousClosingMileage = table.Column<int>(type: "integer", nullable: true),
                    StartingMileage = table.Column<int>(type: "integer", nullable: true),
                    ClosingMileage = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CorrectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrectedByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CorrectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanRouteDayMileageCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VanRouteDayMileageCorrections_Users_CorrectedByUserId",
                        column: x => x.CorrectedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VanRouteDayMileageCorrections_VanRouteDays_VanRouteDayId",
                        column: x => x.VanRouteDayId,
                        principalTable: "VanRouteDays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDayMileageCorrections_CorrectedByUserId",
                table: "VanRouteDayMileageCorrections",
                column: "CorrectedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VanRouteDayMileageCorrections_VanRouteDayId_CorrectedAt",
                table: "VanRouteDayMileageCorrections",
                columns: new[] { "VanRouteDayId", "CorrectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VanRouteDayMileageCorrections");
        }
    }
}
