using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurableApprovalProcess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DocumentKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    OriginatorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginatorName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    OriginatorRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FromWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ToWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    StageSnapshotsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    GeneratedDocumentEntry = table.Column<int>(type: "integer", nullable: true),
                    GeneratedDocumentNumber = table.Column<int>(type: "integer", nullable: true),
                    GeneratedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalStageDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalsRequired = table.Column<int>(type: "integer", nullable: false),
                    RejectionsRequired = table.Column<int>(type: "integer", nullable: false),
                    AuthorizerUserIdsJson = table.Column<string>(type: "text", nullable: false),
                    AuthorizerRolesJson = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalStageDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalTemplateDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DocumentType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    OriginatorUserIdsJson = table.Column<string>(type: "text", nullable: false),
                    OriginatorRolesJson = table.Column<string>(type: "text", nullable: false),
                    StageIdsJson = table.Column<string>(type: "text", nullable: false),
                    FromWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ToWarehouse = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalTemplateDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApprovalRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AuthorizerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizerName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    AuthorizerRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Remarks = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalDecisions_ApprovalRequests_ApprovalRequestId",
                        column: x => x.ApprovalRequestId,
                        principalTable: "ApprovalRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisions_ApprovalRequestId_StageId_AuthorizerUserId",
                table: "ApprovalDecisions",
                columns: new[] { "ApprovalRequestId", "StageId", "AuthorizerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_DocumentType_DocumentKey",
                table: "ApprovalRequests",
                columns: new[] { "DocumentType", "DocumentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_Status_CreatedAtUtc",
                table: "ApprovalRequests",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalStageDefinitions_Name",
                table: "ApprovalStageDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTemplateDefinitions_DocumentType_IsActive_Priority",
                table: "ApprovalTemplateDefinitions",
                columns: new[] { "DocumentType", "IsActive", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTemplateDefinitions_Name",
                table: "ApprovalTemplateDefinitions",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalDecisions");

            migrationBuilder.DropTable(
                name: "ApprovalStageDefinitions");

            migrationBuilder.DropTable(
                name: "ApprovalTemplateDefinitions");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");
        }
    }
}
