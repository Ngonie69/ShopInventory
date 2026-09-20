using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopInventory.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetTelematics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelematicsVehicles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationNormalized = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Registration = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CartrackVehicleId = table.Column<long>(type: "bigint", nullable: true),
                    TerminalSerial = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ClientVehicleName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Model = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    IsUnderMaintenance = table.Column<bool>(type: "boolean", nullable: false),
                    TerminalInRepair = table.Column<bool>(type: "boolean", nullable: false),
                    HasFuelCanbusConsumed = table.Column<bool>(type: "boolean", nullable: false),
                    HasFuelCanbusLevel = table.Column<bool>(type: "boolean", nullable: false),
                    HasFuelAnalogLevel = table.Column<bool>(type: "boolean", nullable: false),
                    HasTemperatureProbe = table.Column<bool>(type: "boolean", nullable: true),
                    LastTemperatureSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActiveInFleet = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelematicsVehicles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleDayRollups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationNormalized = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TradingDate = table.Column<DateTime>(type: "date", nullable: false),
                    FirstIgnitionOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstDepartureUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FirstDepartureLatitude = table.Column<double>(type: "double precision", nullable: true),
                    FirstDepartureLongitude = table.Column<double>(type: "double precision", nullable: true),
                    LastIgnitionOffUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IgnitionCycleCount = table.Column<int>(type: "integer", nullable: true),
                    DrivingSeconds = table.Column<int>(type: "integer", nullable: true),
                    IdleSeconds = table.Column<int>(type: "integer", nullable: true),
                    OdometerStartMetres = table.Column<long>(type: "bigint", nullable: true),
                    OdometerEndMetres = table.Column<long>(type: "bigint", nullable: true),
                    DistanceMetres = table.Column<long>(type: "bigint", nullable: true),
                    OdometerWasReset = table.Column<bool>(type: "boolean", nullable: false),
                    TerminalChanged = table.Column<bool>(type: "boolean", nullable: false),
                    FuelConsumedLitres = table.Column<decimal>(type: "numeric", nullable: true),
                    FuelLevelStartLitres = table.Column<decimal>(type: "numeric", nullable: true),
                    FuelLevelEndLitres = table.Column<decimal>(type: "numeric", nullable: true),
                    EstimatedFuelUsedLitres = table.Column<decimal>(type: "numeric", nullable: true),
                    FuelIsCalibrated = table.Column<bool>(type: "boolean", nullable: true),
                    FuelReadingsAccurate = table.Column<bool>(type: "boolean", nullable: true),
                    FuelFillCount = table.Column<int>(type: "integer", nullable: true),
                    FuelFilledLitres = table.Column<decimal>(type: "numeric", nullable: true),
                    TemperatureChannel = table.Column<byte>(type: "smallint", nullable: true),
                    TemperatureSampleCount = table.Column<int>(type: "integer", nullable: true),
                    TemperatureMinC = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    TemperatureMaxC = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    TemperatureAvgC = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    TemperatureFirstSampleUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TemperatureLastSampleUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LimitMinC = table.Column<decimal>(type: "numeric(4,1)", nullable: true),
                    LimitMaxC = table.Column<decimal>(type: "numeric(4,1)", nullable: true),
                    MinutesAboveMaxLimit = table.Column<int>(type: "integer", nullable: true),
                    MinutesBelowMinLimit = table.Column<int>(type: "integer", nullable: true),
                    HasActivity = table.Column<bool>(type: "boolean", nullable: false),
                    HasOdometer = table.Column<bool>(type: "boolean", nullable: false),
                    HasFuel = table.Column<bool>(type: "boolean", nullable: false),
                    HasTemperature = table.Column<bool>(type: "boolean", nullable: false),
                    BuiltAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDayRollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleLiveStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationNormalized = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EventAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PolledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    PositionDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SpeedKph = table.Column<int>(type: "integer", nullable: true),
                    Bearing = table.Column<int>(type: "integer", nullable: true),
                    IgnitionOn = table.Column<bool>(type: "boolean", nullable: true),
                    Idling = table.Column<bool>(type: "boolean", nullable: true),
                    OdometerMetres = table.Column<long>(type: "bigint", nullable: true),
                    Temp1C = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    Temp2C = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    Temp3C = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    Temp4C = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    DriverName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleLiveStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleTemperatureSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegistrationNormalized = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TradingDate = table.Column<DateTime>(type: "date", nullable: false),
                    Channel = table.Column<byte>(type: "smallint", nullable: false),
                    EventAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TemperatureC = table.Column<decimal>(type: "numeric(5,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleTemperatureSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelematicsVehicles_CartrackVehicleId",
                table: "TelematicsVehicles",
                column: "CartrackVehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_TelematicsVehicles_RegistrationNormalized",
                table: "TelematicsVehicles",
                column: "RegistrationNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDayRollups_RegistrationNormalized_TradingDate",
                table: "VehicleDayRollups",
                columns: new[] { "RegistrationNormalized", "TradingDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDayRollups_TradingDate",
                table: "VehicleDayRollups",
                column: "TradingDate");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleLiveStatuses_RegistrationNormalized",
                table: "VehicleLiveStatuses",
                column: "RegistrationNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleTemperatureSamples_RegistrationNormalized_Channel_Ev~",
                table: "VehicleTemperatureSamples",
                columns: new[] { "RegistrationNormalized", "Channel", "EventAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleTemperatureSamples_RegistrationNormalized_TradingDate",
                table: "VehicleTemperatureSamples",
                columns: new[] { "RegistrationNormalized", "TradingDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelematicsVehicles");

            migrationBuilder.DropTable(
                name: "VehicleDayRollups");

            migrationBuilder.DropTable(
                name: "VehicleLiveStatuses");

            migrationBuilder.DropTable(
                name: "VehicleTemperatureSamples");
        }
    }
}
