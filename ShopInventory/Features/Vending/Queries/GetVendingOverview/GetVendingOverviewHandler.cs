using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.Vending.Queries.GetVendingOverview;

/// <summary>
/// Reads the vending operation: the <see cref="ApplicationRoles.CartVendor"/> accounts, and the vendors
/// under their business partners.
/// </summary>
/// <remarks>
/// A vendor is a route customer and shares that table with the van routes' shops, so "is a vendor" is
/// decided here by the business partner it sits under — one a vending account sells on — rather than
/// by anything on the row. A partner shared by a vending account and a van would put that van's shops
/// on this page as well; that is a configuration nobody runs, and showing them is the honest answer if
/// someone does.
/// </remarks>
public sealed class GetVendingOverviewHandler(ApplicationDbContext context)
    : IRequestHandler<GetVendingOverviewQuery, ErrorOr<VendingOverviewDto>>
{
    public async Task<ErrorOr<VendingOverviewDto>> Handle(
        GetVendingOverviewQuery query,
        CancellationToken cancellationToken)
    {
        var users = await context.Users
            .AsNoTracking()
            .Include(user => user.Shop)
            .Where(user => user.Role == ApplicationRoles.CartVendor)
            .ToListAsync(cancellationToken);

        var businessPartnerCodes = users
            .Select(user => user.AssignedBusinessPartnerCode?.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vendors = businessPartnerCodes.Count == 0
            ? []
            : await context.RouteCustomers
                .AsNoTracking()
                .Where(customer => businessPartnerCodes.Contains(customer.AssignedBusinessPartnerCode))
                .OrderBy(customer => customer.AssignedBusinessPartnerCode)
                .ThenBy(customer => customer.Surname ?? customer.Name)
                .ThenBy(customer => customer.Name)
                .Select(customer => new RouteCustomerDto
                {
                    Id = customer.Id,
                    AssignedBusinessPartnerCode = customer.AssignedBusinessPartnerCode,
                    Code = customer.Code,
                    Name = customer.Name,
                    Surname = customer.Surname,
                    Phone = customer.Phone,
                    Email = customer.Email,
                    Address = customer.Address,
                    VatNumber = customer.VatNumber,
                    IsActive = customer.IsActive,
                    CreatedByUserId = customer.CreatedByUserId,
                    CreatedByUserName = customer.CreatedByUser != null ? customer.CreatedByUser.Username : null,
                    CreatedAt = customer.CreatedAt,
                    UpdatedAt = customer.UpdatedAt
                })
                .ToListAsync(cancellationToken);

        var accounts = users
            .Select(user =>
            {
                var businessPartnerCode = user.AssignedBusinessPartnerCode?.Trim();
                var warehouseCodes = user.GetWarehouseCodes()
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new VendingAccountDto
                {
                    UserId = user.Id,
                    Username = user.Username,
                    FullName = FullName(user),
                    IsActive = user.IsActive,
                    BusinessPartnerCode = string.IsNullOrWhiteSpace(businessPartnerCode) ? null : businessPartnerCode,
                    CostCentreCode = string.IsNullOrWhiteSpace(user.AssignedCostCentreCode) ? null : user.AssignedCostCentreCode.Trim(),
                    WarehouseCode = warehouseCodes.Count == 1 ? warehouseCodes[0] : null,
                    ActiveVendorCount = string.IsNullOrWhiteSpace(businessPartnerCode)
                        ? 0
                        : vendors.Count(vendor => vendor.IsActive &&
                            string.Equals(vendor.AssignedBusinessPartnerCode, businessPartnerCode, StringComparison.OrdinalIgnoreCase)),
                    LastLoginAt = user.LastLoginAt,
                    SetupProblem = SetupProblem(user)
                };
            })
            .OrderByDescending(account => account.IsActive)
            .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var highestVendorNumbers = await VendingDepots.HighestNumbersAsync(context, cancellationToken);

        var depots = accounts
            .Where(account => account.BusinessPartnerCode is not null)
            .GroupBy(account => account.BusinessPartnerCode!, StringComparer.OrdinalIgnoreCase)
            .Select(group => Depot(group.Key, group.ToList(), vendors))
            .OrderBy(depot => depot.BusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var depot in depots)
        {
            // The same rule adding a vendor applies, so the prefix shown is the one the save enforces.
            var rule = VendingDepots.Rule(
                depot.BusinessPartnerCode,
                users
                    .Where(user => string.Equals(user.AssignedBusinessPartnerCode?.Trim(), depot.BusinessPartnerCode, StringComparison.OrdinalIgnoreCase))
                    .Select(user => new VendingDepots.CashierAssignment(depot.BusinessPartnerCode, user.AssignedWarehouseCodes, user.IsActive))
                    .ToList());

            depot.VendorCodePrefix = rule.Prefix;
            depot.VendorCodeProblem = rule.Problem;
            depot.NextVendorCode = rule.Prefix is null ? null : VendingDepots.NextCode(rule.Prefix, highestVendorNumbers);
        }

        return new VendingOverviewDto { Depots = depots, Accounts = accounts, Vendors = vendors };
    }

    /// <summary>
    /// One depot, from the cashiers that sell on its business partner. Only active cashiers are compared:
    /// a closed account still holding last year's warehouse sells nothing and is not a disagreement.
    /// </summary>
    private static VendingDepotDto Depot(
        string businessPartnerCode,
        List<VendingAccountDto> cashiers,
        List<RouteCustomerDto> vendors)
    {
        var active = cashiers.Where(cashier => cashier.IsActive).ToList();
        var compared = active.Count > 0 ? active : cashiers;

        var warehouses = DistinctCodes(compared.Select(cashier => cashier.WarehouseCode));
        var costCentres = DistinctCodes(compared.Select(cashier => cashier.CostCentreCode));

        var depotVendors = vendors
            .Where(vendor => string.Equals(vendor.AssignedBusinessPartnerCode, businessPartnerCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? problem = null;
        if (active.Count > 1 && warehouses.Count > 1)
        {
            problem = $"Its cashiers draw from {warehouses.Count} different warehouses ({string.Join(", ", warehouses)}), so one depot's stock is split.";
        }
        else if (active.Count > 1 && costCentres.Count > 1)
        {
            problem = $"Its cashiers book to {costCentres.Count} different cost centres ({string.Join(", ", costCentres)}), so one depot's takings are split.";
        }

        return new VendingDepotDto
        {
            BusinessPartnerCode = businessPartnerCode,
            WarehouseCodes = warehouses,
            CostCentreCodes = costCentres,
            CashierCount = cashiers.Count,
            ActiveCashierCount = active.Count,
            VendorCount = depotVendors.Count,
            ActiveVendorCount = depotVendors.Count(vendor => vendor.IsActive),
            SetupProblem = problem
        };
    }

    private static List<string> DistinctCodes(IEnumerable<string?> codes) =>
        codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// What stops an active account selling, taken from the resolver the sale itself runs, so this page
    /// cannot call an account ready that the till would refuse. The cost centre is added because account
    /// creation requires it of this role even though the resolver lets a sale through without one.
    /// </summary>
    private static string? SetupProblem(User user)
    {
        if (!user.IsActive)
        {
            return null;
        }

        var resolved = SellingAccountResolver.Resolve(user);
        if (resolved.IsError)
        {
            return resolved.FirstError.Description;
        }

        return string.IsNullOrWhiteSpace(user.AssignedCostCentreCode)
            ? "No cost centre is assigned, so its invoices book to SAP's default."
            : null;
    }

    private static string? FullName(User user)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();
        return name.Length == 0 ? null : name;
    }
}
