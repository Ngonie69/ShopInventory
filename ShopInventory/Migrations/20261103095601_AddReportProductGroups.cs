using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddReportProductGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReportProductGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportProductGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportProductGroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportProductGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportProductGroupMembers_ReportProductGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ReportProductGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportProductGroupMembers_GroupId_Kind_Code",
                table: "ReportProductGroupMembers",
                columns: new[] { "GroupId", "Kind", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportProductGroups_Name",
                table: "ReportProductGroups",
                column: "Name",
                unique: true);

            // The groups the October 2026 month-on-month report was first asked for. Seeded here rather
            // than on start-up so a group an administrator deletes stays deleted. Kind 0 = item, 1 = SAP
            // item group.
            migrationBuilder.Sql("""
                WITH g AS (
                    INSERT INTO "ReportProductGroups" ("Name", "Description", "SortOrder", "IsActive", "CreatedAt", "UpdatedBy")
                    VALUES ('Complimentary lines', 'Soft drinks, juices, snacks and bread: SAP item group 150, Complimentary Products.', 1, TRUE, now(), 'seed')
                    RETURNING "Id")
                INSERT INTO "ReportProductGroupMembers" ("GroupId", "Kind", "Code", "Name")
                SELECT g."Id", 1, '150', 'Complimentary Products' FROM g;

                WITH g AS (
                    INSERT INTO "ReportProductGroups" ("Name", "Description", "SortOrder", "IsActive", "CreatedAt", "UpdatedBy")
                    VALUES ('Shamiso new flavours', 'The flavoured 5 litre Shamiso ice creams added in 2026.', 2, TRUE, now(), 'seed')
                    RETURNING "Id")
                INSERT INTO "ReportProductGroupMembers" ("GroupId", "Kind", "Code", "Name")
                SELECT g."Id", 0, v.code, v.name FROM g, (VALUES
                    ('ICS024', '5 Litre Shamiso Vanilla'),
                    ('ICS025', '5 Litre Shamiso Blueberry'),
                    ('ICS026', '5 Litre Shamiso Bubblegum'),
                    ('ICS027', '5 Litre Shamiso Banana')) AS v(code, name);

                WITH g AS (
                    INSERT INTO "ReportProductGroups" ("Name", "Description", "SortOrder", "IsActive", "CreatedAt", "UpdatedBy")
                    VALUES ('Shamiso ice cream (all)', 'Every Shamiso ice cream, old range and new flavours.', 3, TRUE, now(), 'seed')
                    RETURNING "Id")
                INSERT INTO "ReportProductGroupMembers" ("GroupId", "Kind", "Code", "Name")
                SELECT g."Id", 0, v.code, v.name FROM g, (VALUES
                    ('ICS001', '125ml Shamiso Icecream'),
                    ('ICS002', '500ml Shamiso Icecream'),
                    ('ICS003', '5 Litre Shamiso Icecream'),
                    ('ICS017', '300ml Shamiso Cortina Icecream'),
                    ('ICS019', '2 Litre Shamiso Cortina Icecream'),
                    ('ICC004', 'Icecream Cone Shamiso'),
                    ('ICS024', '5 Litre Shamiso Vanilla'),
                    ('ICS025', '5 Litre Shamiso Blueberry'),
                    ('ICS026', '5 Litre Shamiso Bubblegum'),
                    ('ICS027', '5 Litre Shamiso Banana')) AS v(code, name);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportProductGroupMembers");

            migrationBuilder.DropTable(
                name: "ReportProductGroups");
        }
    }
}
