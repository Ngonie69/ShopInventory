using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ShopInventory.Web.Data;

#nullable disable

namespace ShopInventory.Web.Migrations
{
    /// <summary>
    /// Replaces the UTC whole-hour send time on POD report email schedules with a minute-of-day
    /// offset in the business timezone (CAT, UTC+2). Existing rows are converted by adding the
    /// +2 offset, rolling the weekday / day-of-month forward for late-evening UTC hours that land
    /// on the next local day.
    /// </summary>
    [DbContext(typeof(WebAppDbContext))]
    [Migration("20260708120000_UsePodScheduleLocalSendTime")]
    public partial class UsePodScheduleLocalSendTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SendMinuteOfDay",
                table: "PodReportEmailSchedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Right-hand side reads the pre-UPDATE values, so the day shifts and the new
            // send time can be derived from "SendHourUtc" in a single statement.
            migrationBuilder.Sql(
                """
                UPDATE "PodReportEmailSchedules"
                SET "SendMinuteOfDay" = ((("SendHourUtc" + 2) % 24) * 60),
                    "DayOfWeek" = CASE
                        WHEN "DayOfWeek" IS NOT NULL AND "SendHourUtc" >= 22 THEN ("DayOfWeek" + 1) % 7
                        ELSE "DayOfWeek"
                    END,
                    "DayOfMonth" = CASE
                        WHEN "DayOfMonth" IS NOT NULL AND "SendHourUtc" >= 22 THEN LEAST("DayOfMonth" + 1, 31)
                        ELSE "DayOfMonth"
                    END;
                """);

            migrationBuilder.DropColumn(
                name: "SendHourUtc",
                table: "PodReportEmailSchedules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SendHourUtc",
                table: "PodReportEmailSchedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Minutes are lost on the way back down; the send hour is floored.
            migrationBuilder.Sql(
                """
                UPDATE "PodReportEmailSchedules"
                SET "SendHourUtc" = ((("SendMinuteOfDay" / 60) - 2 + 24) % 24),
                    "DayOfWeek" = CASE
                        WHEN "DayOfWeek" IS NOT NULL AND ("SendMinuteOfDay" / 60) < 2 THEN ("DayOfWeek" + 6) % 7
                        ELSE "DayOfWeek"
                    END,
                    "DayOfMonth" = CASE
                        WHEN "DayOfMonth" IS NOT NULL AND ("SendMinuteOfDay" / 60) < 2 THEN GREATEST("DayOfMonth" - 1, 1)
                        ELSE "DayOfMonth"
                    END;
                """);

            migrationBuilder.DropColumn(
                name: "SendMinuteOfDay",
                table: "PodReportEmailSchedules");
        }
    }
}
