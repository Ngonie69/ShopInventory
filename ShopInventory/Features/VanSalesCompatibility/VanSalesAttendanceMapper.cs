using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Features.Timesheets.Commands.CheckIn;
using ShopInventory.Features.Timesheets.Commands.CheckOut;
using ShopInventory.Features.Timesheets.Queries.GetActiveCheckIn;
using ShopInventory.Features.Timesheets.Queries.GetTimesheets;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility;

public static class VanSalesAttendanceMapper
{
    public static VanSalesAttendanceListResponse MapListResponse(
        TimesheetListResult result,
        User user)
    {
        var records = BuildApiRecords(result.Entries, user);

        return new VanSalesAttendanceListResponse
        {
            Status = 1,
            Message = "Attendance records retrieved successfully.",
            Data = new VanSalesAttendancePaginatedData
            {
                CurrentPage = result.Page,
                Data = records,
                Total = records.Count,
                PerPage = result.PageSize
            }
        };
    }

    public static VanSalesAttendanceByDateResponse MapByDateResponse(
        IReadOnlyCollection<TimesheetEntryDto> entries,
        User user)
    {
        return new VanSalesAttendanceByDateResponse
        {
            Status = 1,
            Message = "Attendance records retrieved successfully.",
            Data = BuildApiRecords(entries, user)
        };
    }

    public static VanSalesAttendanceStatusResponse MapStatusResponse(
        ActiveCheckInResult? activeCheckIn,
        User user)
    {
        var hasOpenCheckins = activeCheckIn is not null;
        var userId = VanSalesCompatibilityMapper.EncodeCompatibilityId(user.Id.ToString());
        var vanCode = ResolveVanCode(user, null);

        return new VanSalesAttendanceStatusResponse
        {
            Status = 1,
            Message = hasOpenCheckins
                ? "Active attendance status retrieved successfully."
                : "No active attendance record found.",
            Data = new VanSalesAttendanceStatusData
            {
                HasOpenCheckins = hasOpenCheckins,
                OpenCheckins = hasOpenCheckins
                    ?
                    [
                        new VanSalesAttendanceStatusCheckIn
                        {
                            Id = BuildRecordId(activeCheckIn!.Id, "IN"),
                            Date = FormatEventDate(activeCheckIn.CheckInTime),
                            Type = "IN",
                            UserId = userId,
                            CustomerId = VanSalesCompatibilityMapper.EncodeCompatibilityId(activeCheckIn.CustomerCode),
                            Van = vanCode,
                            Latitude = FormatCoordinate(activeCheckIn.Latitude),
                            Longitude = FormatCoordinate(activeCheckIn.Longitude),
                            CreatedAt = FormatTimestamp(activeCheckIn.CheckInTime),
                            UpdatedAt = FormatTimestamp(activeCheckIn.CheckInTime),
                            Customer = new VanSalesAttendanceCustomerDto
                            {
                                Id = VanSalesCompatibilityMapper.EncodeCompatibilityId(activeCheckIn.CustomerCode),
                                CustomerCode = activeCheckIn.CustomerCode,
                                CustomerName = activeCheckIn.CustomerName,
                                CustomerAddress = null
                            }
                        }
                    ]
                    : []
            }
        };
    }

    public static VanSalesAttendanceCheckResponse MapCheckInResponse(
        CheckInResult result,
        User user,
        VanSalesShopDto? shop,
        string? requestedVan)
    {
        var record = new VanSalesAttendanceApiRecord
        {
            Id = BuildRecordId(result.Id, "IN"),
            Date = FormatEventDate(result.CheckInTime),
            Type = "IN",
            UserId = VanSalesCompatibilityMapper.EncodeCompatibilityId(user.Id.ToString()),
            CustomerId = VanSalesCompatibilityMapper.EncodeCompatibilityId(result.CustomerCode),
            Van = BuildVanReference(user, requestedVan),
            Latitude = FormatCoordinate(result.Latitude),
            Longitude = FormatCoordinate(result.Longitude),
            CreatedAt = FormatTimestamp(result.CheckInTime),
            UpdatedAt = FormatTimestamp(result.CheckInTime),
            Customer = BuildCustomerReference(result.CustomerCode, result.CustomerName, shop?.Address)
        };

        return new VanSalesAttendanceCheckResponse
        {
            Status = 1,
            Message = "Attendance recorded successfully.",
            Data = record
        };
    }

    public static VanSalesAttendanceCheckResponse MapCheckOutResponse(
        CheckOutResult result,
        User user,
        string? requestedVan)
    {
        var record = new VanSalesAttendanceApiRecord
        {
            Id = BuildRecordId(result.Id, "OUT"),
            Date = FormatEventDate(result.CheckInTime),
            Type = "OUT",
            UserId = VanSalesCompatibilityMapper.EncodeCompatibilityId(user.Id.ToString()),
            CustomerId = VanSalesCompatibilityMapper.EncodeCompatibilityId(result.CustomerCode),
            Van = BuildVanReference(user, requestedVan),
            Latitude = FormatCoordinate(result.Latitude),
            Longitude = FormatCoordinate(result.Longitude),
            CreatedAt = FormatTimestamp(result.CheckOutTime),
            UpdatedAt = FormatTimestamp(result.CheckOutTime),
            Customer = BuildCustomerReference(result.CustomerCode, result.CustomerName, null)
        };

        return new VanSalesAttendanceCheckResponse
        {
            Status = 1,
            Message = "Attendance updated successfully.",
            Data = record
        };
    }

    public static VanSalesAttendanceListResponse MapListFailure(string message)
    {
        return new VanSalesAttendanceListResponse
        {
            Status = 0,
            Message = message,
            Data = new VanSalesAttendancePaginatedData
            {
                CurrentPage = 1,
                Data = [],
                Total = 0,
                PerPage = 0
            }
        };
    }

    public static VanSalesAttendanceByDateResponse MapByDateFailure(string message)
    {
        return new VanSalesAttendanceByDateResponse
        {
            Status = 0,
            Message = message,
            Data = []
        };
    }

    public static VanSalesAttendanceStatusResponse MapStatusFailure(string message)
    {
        return new VanSalesAttendanceStatusResponse
        {
            Status = 0,
            Message = message,
            Data = new VanSalesAttendanceStatusData
            {
                HasOpenCheckins = false,
                OpenCheckins = []
            }
        };
    }

    public static VanSalesAttendanceCheckResponse MapCheckFailure(string message)
    {
        return new VanSalesAttendanceCheckResponse
        {
            Status = 0,
            Message = message,
            Data = null
        };
    }

    private static List<VanSalesAttendanceApiRecord> BuildApiRecords(
        IEnumerable<TimesheetEntryDto> entries,
        User user)
    {
        var records = new List<VanSalesAttendanceApiRecord>();
        var userId = VanSalesCompatibilityMapper.EncodeCompatibilityId(user.Id.ToString());
        var vanReference = BuildVanReference(user, null);

        foreach (var entry in entries.OrderByDescending(item => item.CheckInTime).ThenByDescending(item => item.Id))
        {
            var customerId = VanSalesCompatibilityMapper.EncodeCompatibilityId(entry.CustomerCode);
            var eventDate = FormatEventDate(entry.CheckInTime);
            var customer = BuildCustomerReference(entry.CustomerCode, entry.CustomerName, null);

            records.Add(new VanSalesAttendanceApiRecord
            {
                Id = BuildRecordId(entry.Id, "IN"),
                Date = eventDate,
                Type = "IN",
                UserId = userId,
                CustomerId = customerId,
                Van = vanReference,
                Latitude = FormatCoordinate(entry.CheckInLatitude),
                Longitude = FormatCoordinate(entry.CheckInLongitude),
                CreatedAt = FormatTimestamp(entry.CheckInTime),
                UpdatedAt = FormatTimestamp(entry.CheckOutTime ?? entry.CheckInTime),
                Customer = customer
            });

            if (!entry.CheckOutTime.HasValue)
            {
                continue;
            }

            records.Add(new VanSalesAttendanceApiRecord
            {
                Id = BuildRecordId(entry.Id, "OUT"),
                Date = eventDate,
                Type = "OUT",
                UserId = userId,
                CustomerId = customerId,
                Van = vanReference,
                Latitude = FormatCoordinate(entry.CheckOutLatitude),
                Longitude = FormatCoordinate(entry.CheckOutLongitude),
                CreatedAt = FormatTimestamp(entry.CheckOutTime.Value),
                UpdatedAt = FormatTimestamp(entry.CheckOutTime.Value),
                Customer = customer
            });
        }

        return records;
    }

    private static VanSalesAttendanceVanDto BuildVanReference(User user, string? requestedVan)
    {
        var vanCode = ResolveVanCode(user, requestedVan);

        return new VanSalesAttendanceVanDto
        {
            Id = string.IsNullOrWhiteSpace(vanCode)
                ? 0
                : VanSalesCompatibilityMapper.EncodeCompatibilityId(vanCode),
            VCode = vanCode,
            VBusinessPartnerName = vanCode
        };
    }

    private static VanSalesAttendanceCustomerDto BuildCustomerReference(
        string customerCode,
        string customerName,
        string? address)
    {
        return new VanSalesAttendanceCustomerDto
        {
            Id = VanSalesCompatibilityMapper.EncodeCompatibilityId(customerCode),
            CustomerCode = customerCode,
            CustomerName = customerName,
            CustomerAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim()
        };
    }

    private static int BuildRecordId(int timesheetEntryId, string type)
    {
        return string.Equals(type, "OUT", StringComparison.OrdinalIgnoreCase)
            ? checked(timesheetEntryId * 2)
            : checked((timesheetEntryId * 2) - 1);
    }

    private static string ResolveVanCode(User user, string? requestedVan)
    {
        if (!string.IsNullOrWhiteSpace(requestedVan))
        {
            return requestedVan.Trim();
        }

        return VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user)
            ?? user.AssignedSection?.Trim()
            ?? string.Empty;
    }

    private static string FormatEventDate(DateTime utcTimestamp)
    {
        return AuditService.ToCAT(utcTimestamp)
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(DateTime utcTimestamp)
    {
        return AuditService.ToCAT(utcTimestamp)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatCoordinate(double? value)
    {
        return value.HasValue
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }
}