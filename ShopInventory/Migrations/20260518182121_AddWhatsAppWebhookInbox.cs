using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppWebhookInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WhatsAppWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdempotencyKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DeliveryId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SessionName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    MessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChatId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    SenderNumber = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SenderDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    MessageType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsFromMe = table.Column<bool>(type: "boolean", nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: true),
                    SourcePath = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RawPayload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_ChatId",
                table: "WhatsAppWebhookEvents",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_DeliveryId",
                table: "WhatsAppWebhookEvents",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_EventType",
                table: "WhatsAppWebhookEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_IdempotencyKey",
                table: "WhatsAppWebhookEvents",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_MessageId",
                table: "WhatsAppWebhookEvents",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_OccurredAtUtc",
                table: "WhatsAppWebhookEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_ReceivedAtUtc",
                table: "WhatsAppWebhookEvents",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppWebhookEvents_SessionName",
                table: "WhatsAppWebhookEvents",
                column: "SessionName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppWebhookEvents");
        }
    }
}
