using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVanSalesCustomerOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VanSalesOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VanSalesCustomerAccountId = table.Column<int>(type: "integer", nullable: false),
                    RouteCustomerId = table.Column<int>(type: "integer", nullable: false),
                    RouteCustomerCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RouteCustomerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssignedBusinessPartnerCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RouteCode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    RouteName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RequestedVisitDate = table.Column<DateTime>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SubTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    DocTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CustomerNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ClientRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeviceInfo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    ConvertedSalesOrderId = table.Column<int>(type: "integer", nullable: true),
                    ConvertedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanSalesOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VanSalesOrders_RouteCustomers_RouteCustomerId",
                        column: x => x.RouteCustomerId,
                        principalTable: "RouteCustomers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VanSalesOrders_SalesOrders_ConvertedSalesOrderId",
                        column: x => x.ConvertedSalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VanSalesOrders_VanSalesCustomerAccounts_VanSalesCustomerAcc~",
                        column: x => x.VanSalesCustomerAccountId,
                        principalTable: "VanSalesCustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VanSalesOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VanSalesOrderId = table.Column<int>(type: "integer", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ItemCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ItemDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UoMCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    QuantityOrdered = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    QuantityFulfilled = table.Column<decimal>(type: "numeric(18,3)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VanSalesOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VanSalesOrderLines_VanSalesOrders_VanSalesOrderId",
                        column: x => x.VanSalesOrderId,
                        principalTable: "VanSalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesOrderLines_VanSalesOrderId",
                table: "VanSalesOrderLines",
                column: "VanSalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesOrders_ClientRequestId",
                table: "VanSalesOrders",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesOrders_ConvertedSalesOrderId",
                table: "VanSalesOrders",
                column: "ConvertedSalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesOrders_OrderNumber",
                table: "VanSalesOrders",
                column: "OrderNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesOrders_RequestedVisitDate_Status",
                table: "VanSalesOrders",
                columns: new[] { "RequestedVisitDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesOrders_RouteCustomerId",
                table: "VanSalesOrders",
                column: "RouteCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_VanSalesOrders_VanSalesCustomerAccountId_ReceivedAtUtc",
                table: "VanSalesOrders",
                columns: new[] { "VanSalesCustomerAccountId", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VanSalesOrderLines");

            migrationBuilder.DropTable(
                name: "VanSalesOrders");
        }
    }
}
