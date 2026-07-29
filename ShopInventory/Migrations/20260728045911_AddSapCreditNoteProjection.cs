using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddSapCreditNoteProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SapCreditNoteSnapshots",
                columns: table => new
                {
                    SapDocEntry = table.Column<int>(type: "integer", nullable: false),
                    SapDocNum = table.Column<int>(type: "integer", nullable: false),
                    DocDate = table.Column<DateTime>(type: "date", nullable: false),
                    CardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CardName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DocCurrency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    DocTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    VatSum = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    DocumentStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    IsCancelled = table.Column<bool>(type: "boolean", nullable: false),
                    SapUpdateDate = table.Column<DateTime>(type: "date", nullable: true),
                    LastSeenInSapAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SapCreditNoteSnapshots", x => x.SapDocEntry);
                });

            migrationBuilder.CreateTable(
                name: "SapCreditNoteLineSnapshots",
                columns: table => new
                {
                    CreditNoteDocEntry = table.Column<int>(type: "integer", nullable: false),
                    LineNum = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BaseEntry = table.Column<int>(type: "integer", nullable: true),
                    BaseLine = table.Column<int>(type: "integer", nullable: true),
                    BaseType = table.Column<int>(type: "integer", nullable: true),
                    LineTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    VatSum = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreditReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SapCreditNoteLineSnapshots", x => new { x.CreditNoteDocEntry, x.LineNum });
                    table.ForeignKey(
                        name: "FK_SapCreditNoteLineSnapshots_SapCreditNoteSnapshots_CreditNot~",
                        column: x => x.CreditNoteDocEntry,
                        principalTable: "SapCreditNoteSnapshots",
                        principalColumn: "SapDocEntry",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SapCreditNoteLineSnapshots_BaseType_BaseEntry",
                table: "SapCreditNoteLineSnapshots",
                columns: new[] { "BaseType", "BaseEntry" });

            migrationBuilder.CreateIndex(
                name: "IX_SapCreditNoteLineSnapshots_ItemCode",
                table: "SapCreditNoteLineSnapshots",
                column: "ItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_SapCreditNoteSnapshots_DocDate",
                table: "SapCreditNoteSnapshots",
                column: "DocDate");

            migrationBuilder.CreateIndex(
                name: "IX_SapCreditNoteSnapshots_IsCancelled_DocDate",
                table: "SapCreditNoteSnapshots",
                columns: new[] { "IsCancelled", "DocDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SapCreditNoteSnapshots_SapDocNum",
                table: "SapCreditNoteSnapshots",
                column: "SapDocNum");

            migrationBuilder.CreateIndex(
                name: "IX_SapCreditNoteSnapshots_SapUpdateDate",
                table: "SapCreditNoteSnapshots",
                column: "SapUpdateDate");

            migrationBuilder.CreateIndex(
                name: "IX_SapCreditNoteSnapshots_SyncedAtUtc",
                table: "SapCreditNoteSnapshots",
                column: "SyncedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SapCreditNoteLineSnapshots");

            migrationBuilder.DropTable(
                name: "SapCreditNoteSnapshots");
        }
    }
}
