using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Vending.Commands.ImportVendors;

/// <summary>
/// Adds a sheet of vendors to the vending depots, all or nothing.
/// </summary>
/// <remarks>
/// Every row is resolved before anything is written, and every problem is reported against its row, so
/// someone fixing a file sees all of it at once instead of one refusal per upload. A file with any
/// problem saves nothing: half a sheet imported is a sheet nobody can safely upload again.
///
/// Each row is held to <see cref="VendorCodeConvention"/>. A code's prefix names its depot, so the Depot
/// column may be left blank when a code is given; a blank code takes the depot's next number, issued
/// after every code the file names explicitly so that a generated code never lands on one a later row
/// asks for. A code matching a removed vendor at the same depot brings that vendor back, the way adding
/// one by hand does, which keeps a returning vendor's sales history on them.
///
/// Rows only ever add. A code that is already a live vendor is a problem, not an update: a sheet that
/// silently overwrote the phone numbers of vendors already trading is not what "upload vendors" says.
/// </remarks>
public sealed class ImportVendorsHandler(
    ApplicationDbContext context,
    INotificationService notificationService,
    ILogger<ImportVendorsHandler> logger
) : IRequestHandler<ImportVendorsCommand, ErrorOr<ImportVendorsResultDto>>
{
    public async Task<ErrorOr<ImportVendorsResultDto>> Handle(
        ImportVendorsCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null)
        {
            return Errors.RouteCustomers.UserNotFound;
        }

        if (!user.IsActive)
        {
            return Errors.RouteCustomers.UserInactive;
        }

        var depots = await VendingDepots.LoadAsync(context, cancellationToken);
        var highest = await VendingDepots.HighestNumbersAsync(context, cancellationToken);

        var rows = command.Request.Rows
            .Select(row => new Resolution(row))
            .ToList();

        foreach (var row in rows)
        {
            ResolveDepotAndCode(row, depots);
            CheckDetails(row);
        }

        MarkDuplicateCodes(rows);

        var explicitCodes = rows
            .Where(row => row.Code is not null)
            .Select(row => row.Code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = explicitCodes.Count == 0
            ? []
            : await context.RouteCustomers
                .Where(customer => explicitCodes.Contains(customer.Code))
                .ToListAsync(cancellationToken);

        foreach (var row in rows.Where(row => row.Code is not null && row.Depot is not null))
        {
            MatchExisting(row, existing);
        }

        IssueGeneratedCodes(rows, highest);

        var result = new ImportVendorsResultDto
        {
            Rows = rows.Select(row => row.ToResult()).ToList(),
        };
        result.ErrorCount = result.Rows.Count(row => row.Action == ImportVendorActions.Error);
        result.CreateCount = result.Rows.Count(row => row.Action == ImportVendorActions.Create);
        result.RestoreCount = result.Rows.Count(row => row.Action == ImportVendorActions.Restore);

        if (command.Request.ValidateOnly || result.ErrorCount > 0)
        {
            return result;
        }

        var now = DateTime.UtcNow;
        var saved = new List<(Resolution Row, RouteCustomerEntity Entity)>();
        foreach (var row in rows)
        {
            var entity = row.Restores ?? new RouteCustomerEntity
            {
                AssignedBusinessPartnerCode = row.Depot!.BusinessPartnerCode,
                Code = row.Code!,
                CreatedByUserId = user.Id,
                CreatedAt = now,
            };

            entity.Name = row.Name!;
            entity.Surname = row.Surname;
            entity.Phone = row.Phone;
            entity.Email = row.Email;
            entity.Address = row.Address;
            entity.VatNumber = row.VatNumber;
            entity.IsActive = true;

            if (row.Restores is null)
            {
                context.RouteCustomers.Add(entity);
            }
            else
            {
                // CreatedAt stays: it is when the vendor first joined, which the reports date them from.
                entity.UpdatedAt = now;
            }

            saved.Add((row, entity));
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Vendor import of {RowCount} rows collided with a concurrent write", rows.Count);
            return Errors.Vending.ImportCollided;
        }

        logger.LogInformation(
            "Imported {CreateCount} new and {RestoreCount} restored vendors for user {UserId}",
            result.CreateCount,
            result.RestoreCount,
            user.Id);

        for (var index = 0; index < saved.Count; index++)
        {
            result.Rows[index].VendorId = saved[index].Entity.Id;
        }

        result.Imported = true;

        await NotifyDepotsAsync(saved.Select(pair => pair.Entity).ToList(), cancellationToken);

        return result;
    }

    // ── Resolving a row ────────────────────────────────────────────────────

    private static void ResolveDepotAndCode(Resolution row, List<VendingDepotCodeRule> depots)
    {
        VendingDepotCodeRule? named = null;
        if (row.DepotText is not null)
        {
            named = depots.FirstOrDefault(depot =>
                string.Equals(depot.BusinessPartnerCode, row.DepotText, StringComparison.OrdinalIgnoreCase));

            if (named is null)
            {
                var byWarehouse = depots
                    .Where(depot => depot.WarehouseCodes.Contains(row.DepotText, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (byWarehouse.Count > 1)
                {
                    row.Errors.Add($"More than one depot draws from {row.DepotText} ({string.Join(", ", byWarehouse.Select(depot => depot.BusinessPartnerCode))}); name the depot by its business partner code.");
                    return;
                }

                named = byWarehouse.FirstOrDefault();
            }

            if (named is null)
            {
                row.Errors.Add($"'{row.DepotText}' is not a vending depot. Use its business partner code ({string.Join(", ", depots.Select(depot => depot.BusinessPartnerCode).Order())}) or its warehouse.");
                return;
            }
        }

        if (row.Code is null)
        {
            if (named is null)
            {
                row.Errors.Add("Give the vendor a code, or a depot to number them at.");
                return;
            }

            if (named.Prefix is null)
            {
                row.Errors.Add(Errors.Vending.DepotCannotNumberVendors(named.BusinessPartnerCode, named.Problem!).Description);
                return;
            }

            row.Depot = named;
            return;
        }

        if (!VendorCodeConvention.TryParse(row.Code, out var prefix, out var number))
        {
            row.Errors.Add($"'{row.Code}' is not a vendor code. Codes are {VendorCodeConvention.Described} followed by three digits, like VMB001.");
            return;
        }

        row.CodeNumber = number;

        if (named is not null)
        {
            if (named.Prefix is null)
            {
                row.Errors.Add(Errors.Vending.DepotCannotNumberVendors(named.BusinessPartnerCode, named.Problem!).Description);
                return;
            }

            if (!string.Equals(named.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
            {
                row.Errors.Add(Errors.Vending.VendorCodeDoesNotFitDepot(row.Code, named.BusinessPartnerCode, named.Prefix).Description);
                return;
            }

            row.Depot = named;
            return;
        }

        var byPrefix = depots
            .Where(depot => string.Equals(depot.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byPrefix.Count == 1)
        {
            row.Depot = byPrefix[0];
            return;
        }

        var warehouse = VendorCodeConvention.PrefixByWarehouse.First(pair => pair.Value == prefix).Key;
        row.Errors.Add(byPrefix.Count == 0
            ? $"No vending depot draws stock from {warehouse}, where {prefix} codes belong."
            : $"{prefix} codes belong at more than one depot ({string.Join(", ", byPrefix.Select(depot => depot.BusinessPartnerCode))}); fill in the depot.");
    }

    private static void CheckDetails(Resolution row)
    {
        if (row.Name is null)
        {
            row.Errors.Add("A first name is required.");
        }

        CheckLength(row, row.Name, 200, "First name");
        CheckLength(row, row.Surname, 100, "Surname");
        CheckLength(row, row.Phone, 50, "Phone");
        CheckLength(row, row.Email, 255, "Email");
        CheckLength(row, row.Address, 500, "Address");
        CheckLength(row, row.VatNumber, 100, "VAT number");
    }

    private static void CheckLength(Resolution row, string? value, int maxLength, string label)
    {
        if (value is not null && value.Length > maxLength)
        {
            row.Errors.Add($"{label} is longer than {maxLength} characters.");
        }
    }

    private static void MarkDuplicateCodes(List<Resolution> rows)
    {
        foreach (var group in rows
                     .Where(row => row.Code is not null)
                     .GroupBy(row => row.Code!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var row in group)
            {
                var others = group.Where(other => !ReferenceEquals(other, row)).Select(other => other.RowNumber);
                row.Errors.Add($"{row.Code} is also on row {string.Join(", ", others)}.");
            }
        }
    }

    private static void MatchExisting(Resolution row, List<RouteCustomerEntity> existing)
    {
        foreach (var match in existing.Where(customer => string.Equals(customer.Code, row.Code, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.Equals(match.AssignedBusinessPartnerCode, row.Depot!.BusinessPartnerCode, StringComparison.OrdinalIgnoreCase))
            {
                row.Errors.Add(Errors.Vending.VendorCodeTaken(row.Code!, match.AssignedBusinessPartnerCode).Description);
            }
            else if (match.IsActive)
            {
                var name = string.IsNullOrWhiteSpace(match.Surname) ? match.Name : $"{match.Name} {match.Surname}";
                row.Errors.Add($"{row.Code} is already a vendor at {match.AssignedBusinessPartnerCode} ({name}). Edit them on the page instead.");
            }
            else
            {
                row.Restores = match;
            }
        }
    }

    /// <summary>
    /// Numbers the blank-code rows, per prefix, after the highest code issued anywhere and the highest the
    /// file itself names. Rows that already have a problem are left unnumbered: they cannot be saved, and
    /// numbering them would make the next upload's codes shift under the operator.
    /// </summary>
    private static void IssueGeneratedCodes(List<Resolution> rows, Dictionary<string, int> highest)
    {
        var next = new Dictionary<string, int>(highest, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => row.Code is not null && row.Depot?.Prefix is not null))
        {
            next[row.Depot!.Prefix!] = Math.Max(next.GetValueOrDefault(row.Depot.Prefix!), row.CodeNumber);
        }

        foreach (var row in rows.Where(row => row.Code is null && row.Depot?.Prefix is not null && row.Errors.Count == 0))
        {
            var prefix = row.Depot!.Prefix!;
            var number = next.GetValueOrDefault(prefix) + 1;
            if (number > VendorCodeConvention.MaxNumber)
            {
                row.Errors.Add(Errors.Vending.VendorCodesExhausted(prefix).Description);
                continue;
            }

            next[prefix] = number;
            row.Code = VendorCodeConvention.Format(prefix, number);
            row.CodeWasIssued = true;
        }
    }

    // ── Telling the depots ─────────────────────────────────────────────────

    /// <summary>
    /// One notification per cashier per depot, not one per vendor: a sheet of two hundred would otherwise
    /// be two hundred bell entries for every cashier on the depot.
    /// </summary>
    private async Task NotifyDepotsAsync(List<RouteCustomerEntity> vendors, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var depot in vendors.GroupBy(vendor => vendor.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase))
            {
                var recipients = await context.Users
                    .AsNoTracking()
                    .Where(candidate => candidate.IsActive
                        && candidate.AssignedBusinessPartnerCode == depot.Key
                        && candidate.Role != null
                        && ApplicationRoles.RouteCustomerScopedRoles.Contains(candidate.Role)
                        && candidate.Username != null
                        && candidate.Username != string.Empty)
                    .Select(candidate => new { candidate.Id, candidate.Username })
                    .ToListAsync(cancellationToken);

                foreach (var recipient in recipients
                             .GroupBy(candidate => candidate.Username!, StringComparer.OrdinalIgnoreCase)
                             .Select(group => group.First()))
                {
                    await notificationService.CreateNotificationAsync(
                        WorkflowNotificationFactory.CreateRouteCustomersImportedNotification(
                            recipient.Id,
                            recipient.Username!,
                            depot.Key,
                            depot.Count()),
                        cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish vendor import notifications for {VendorCount} vendors", vendors.Count);
        }
    }

    private sealed class Resolution(ImportVendorRow row)
    {
        public int RowNumber { get; } = row.RowNumber;
        public string? DepotText { get; } = Clean(row.Depot);
        public string? Code { get; set; } = VendorCodeConvention.Normalize(row.Code);
        public int CodeNumber { get; set; }
        public bool CodeWasIssued { get; set; }
        public string? Name { get; } = Clean(row.Name);
        public string? Surname { get; } = Clean(row.Surname);
        public string? Phone { get; } = Clean(row.Phone);
        public string? Email { get; } = Clean(row.Email);
        public string? Address { get; } = Clean(row.Address);
        public string? VatNumber { get; } = Clean(row.VatNumber);
        public VendingDepotCodeRule? Depot { get; set; }
        public RouteCustomerEntity? Restores { get; set; }
        public List<string> Errors { get; } = [];

        public ImportVendorRowResult ToResult() => new()
        {
            RowNumber = RowNumber,
            Code = Code,
            CodeIssued = CodeWasIssued,
            Depot = Depot?.BusinessPartnerCode,
            Name = Name,
            Surname = Surname,
            Action = Errors.Count > 0
                ? ImportVendorActions.Error
                : Restores is not null ? ImportVendorActions.Restore : ImportVendorActions.Create,
            Errors = [.. Errors],
        };

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
