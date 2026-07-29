using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddCrateTrackingModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrateTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransactionType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InvoiceDocEntry = table.Column<int>(type: "integer", nullable: true),
                    InvoiceDocNum = table.Column<int>(type: "integer", nullable: true),
                    ShopCardCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ShopName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExpectedQuantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrateTransactions", x => x.Id);
                    table.CheckConstraint("CK_CrateTransactions_ExpectedQuantity_NonNegative", "\"ExpectedQuantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_CrateTransactions_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CrateGrvs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CrateTransactionId = table.Column<int>(type: "integer", nullable: false),
                    GrvNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ExpectedQuantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ActualQuantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VarianceQuantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrateGrvs", x => x.Id);
                    table.CheckConstraint("CK_CrateGrvs_ActualQuantity_NonNegative", "\"ActualQuantity\" >= 0");
                    table.CheckConstraint("CK_CrateGrvs_ExpectedQuantity_NonNegative", "\"ExpectedQuantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_CrateGrvs_CrateTransactions_CrateTransactionId",
                        column: x => x.CrateTransactionId,
                        principalTable: "CrateTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CrateGrvs_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CratePodSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CrateTransactionId = table.Column<int>(type: "integer", nullable: false),
                    SubmissionRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CratePodSubmissions", x => x.Id);
                    table.CheckConstraint("CK_CratePodSubmissions_Quantity_NonNegative", "\"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_CratePodSubmissions_CrateTransactions_CrateTransactionId",
                        column: x => x.CrateTransactionId,
                        principalTable: "CrateTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CratePodSubmissions_Users_SubmittedByUserId",
                        column: x => x.SubmittedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrateGrvs_CrateTransactionId",
                table: "CrateGrvs",
                column: "CrateTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrateGrvs_CreatedAt",
                table: "CrateGrvs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CrateGrvs_CreatedByUserId",
                table: "CrateGrvs",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CrateGrvs_GrvNumber",
                table: "CrateGrvs",
                column: "GrvNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrateGrvs_Status",
                table: "CrateGrvs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CratePodSubmissions_CrateTransactionId_SubmissionRole",
                table: "CratePodSubmissions",
                columns: new[] { "CrateTransactionId", "SubmissionRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CratePodSubmissions_SubmittedAt",
                table: "CratePodSubmissions",
                column: "SubmittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CratePodSubmissions_SubmittedByUserId",
                table: "CratePodSubmissions",
                column: "SubmittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CrateTransactions_CreatedAt",
                table: "CrateTransactions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CrateTransactions_CreatedByUserId",
                table: "CrateTransactions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CrateTransactions_EffectiveDate",
                table: "CrateTransactions",
                column: "EffectiveDate");

            migrationBuilder.CreateIndex(
                name: "IX_CrateTransactions_InvoiceDocEntry",
                table: "CrateTransactions",
                column: "InvoiceDocEntry",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrateTransactions_ShopCardCode",
                table: "CrateTransactions",
                column: "ShopCardCode");

            migrationBuilder.CreateIndex(
                name: "IX_CrateTransactions_TransactionType",
                table: "CrateTransactions",
                column: "TransactionType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrateGrvs");

            migrationBuilder.DropTable(
                name: "CratePodSubmissions");

            migrationBuilder.DropTable(
                name: "CrateTransactions");
        }
    }
}
