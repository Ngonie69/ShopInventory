using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferArrivalNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransferArrivalNotices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferDocEntry = table.Column<int>(type: "integer", nullable: false),
                    TransferDocNum = table.Column<int>(type: "integer", nullable: false),
                    WarehouseCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceWarehouse = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnnouncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferArrivalNotices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferArrivalNotices_TransferDocEntry_WarehouseCode",
                table: "TransferArrivalNotices",
                columns: new[] { "TransferDocEntry", "WarehouseCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransferArrivalNotices");
        }
    }
}
