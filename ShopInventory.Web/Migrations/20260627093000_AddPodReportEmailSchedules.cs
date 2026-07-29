using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using ShopInventory.Web.Data;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(WebAppDbContext))]
    [Migration("20260627093000_AddPodReportEmailSchedules")]
    public partial class AddPodReportEmailSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PodReportEmailSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: true),
                    IntervalDays = table.Column<int>(type: "integer", nullable: true),
                    SendHourUtc = table.Column<int>(type: "integer", nullable: false),
                    ToRecipients = table.Column<string>(type: "text", nullable: false),
                    CcRecipients = table.Column<string>(type: "text", nullable: false),
                    LastSentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnchorDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodReportEmailSchedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PodReportEmailSchedules_Enabled",
                table: "PodReportEmailSchedules",
                column: "Enabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PodReportEmailSchedules");
        }
    }
}
