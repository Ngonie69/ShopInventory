using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.Timesheets.Queries.GetAssignedCustomers;

public sealed class GetAssignedCustomersHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    ILogger<GetAssignedCustomersHandler> logger
) : IRequestHandler<GetAssignedCustomersQuery, ErrorOr<List<AssignedCustomerDto>>>
{
    public async Task<ErrorOr<List<AssignedCustomerDto>>> Handle(
        GetAssignedCustomersQuery request,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user is null)
            return Common.Errors.Errors.Auth.UserNotFound;

        var customerCodes = user.GetCustomerCodes();
        if (customerCodes.Count == 0)
            return new List<AssignedCustomerDto>();

        // Check if user has an active check-in
        var activeCheckInCustomerCode = await db.TimesheetEntries
            .AsNoTracking()
            .Where(t => t.UserId == request.UserId && t.CheckOutTime == null)
            .OrderByDescending(t => t.CheckInTime)
            .ThenByDescending(t => t.Id)
            .Select(t => t.CustomerCode)
            .FirstOrDefaultAsync(cancellationToken);

        // Resolve customer names from SAP
        var results = new List<AssignedCustomerDto>();
        foreach (var code in customerCodes)
        {
            var name = code;
            try
            {
                var bp = await sapClient.GetBusinessPartnerByCodeAsync(code, cancellationToken);
                if (bp?.CardName is not null)
                    name = bp.CardName;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve customer name for {CustomerCode}, using code as fallback", code);
            }

            results.Add(new AssignedCustomerDto(
                code,
                name,
                HasActiveCheckIn: code == activeCheckInCustomerCode));
        }

        return results;
    }
}
