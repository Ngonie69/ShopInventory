using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;
using ShopInventory.Features.Vending;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateVendorForAccount;

/// <summary>
/// Lets a cart-vendor till add a vendor, held to the rules an administrator is held to.
/// </summary>
/// <remarks>
/// <para>
/// The account does not hold <c>customers.create</c>, and that is deliberate rather than an obstacle to
/// work round. The permission also opens <c>POST /api/vending/vendors/import</c>, which names its depot
/// per row, and <c>POST /api/route-customers</c>, which names one in the body — so granting it would let a
/// till add vendors at every depot. This route names none: the depot is the one the account sells on.
/// </para>
/// <para>
/// Once the depot is settled the write is <see cref="CreateRouteCustomerHandler"/>'s, unchanged, so a till
/// gets exactly what the Vending page gets — the depot's <c>VMB</c>/<c>VMP</c>/<c>VMM</c> prefix and next
/// number, a code refused if it does not fit or is held at another depot, and a removed vendor restored
/// rather than forked. Nothing about the convention is restated here.
/// </para>
/// <para>
/// Two things are added in front of it. The field limits, because the shared handler leaves them to the
/// columns and a too-long phone number would otherwise surface as a server error at a counter. And a
/// refusal to list the same person twice: a generated code is new on every request, so a till that
/// retries after losing the reply would otherwise leave the queue's vendor on the list under two codes,
/// with their takings split between them.
/// </para>
/// </remarks>
public sealed class CreateVendorForAccountHandler(
    ApplicationDbContext context,
    INotificationService notificationService,
    ILogger<CreateRouteCustomerHandler> createLogger,
    ILogger<CreateVendorForAccountHandler> logger
) : IRequestHandler<CreateVendorForAccountCommand, ErrorOr<DesktopVendorDto>>
{
    public const int MaxNameLength = 200;
    public const int MaxSurnameLength = 100;
    public const int MaxPhoneLength = 50;
    public const int MaxVatNumberLength = 100;
    public const int MaxCodeLength = 50;

    public async Task<ErrorOr<DesktopVendorDto>> Handle(
        CreateVendorForAccountCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .Include(candidate => candidate.Shop)
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        var assignments = SellingAccountResolver.Resolve(user);
        if (assignments.IsError)
        {
            return assignments.Errors;
        }

        // Only an account that invoices vendors keeps a vendor list. A shop till sells to walk-ins, and a
        // row added under its business partner would be a route customer nothing ever sells to.
        if (!string.Equals(user!.Role, ApplicationRoles.CartVendor, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.Vending.AccountDoesNotKeepVendors;
        }

        var cardCode = assignments.Value.CardCode;

        // The shared handler numbers a vendor only under a business partner it recognises as a depot, and
        // otherwise derives a code from the name. That second path is for van routes' shops, and a till
        // must never reach it: the vendor would be listed under a code that breaks the convention.
        var depot = await VendingDepots.FindAsync(context, cardCode, cancellationToken);
        if (depot is null)
        {
            return Errors.Vending.NotAVendingDepot(cardCode);
        }

        var request = command.Request;
        var name = Clean(request.Name);
        var surname = Clean(request.Surname);
        var phone = Clean(request.Phone);
        var vatNumber = Clean(request.VatNumber);
        var code = Clean(request.Code);

        var problems = new List<Error>();
        if (name is null)
        {
            problems.Add(Errors.RouteCustomers.NameRequired);
        }

        CheckLength(problems, name, MaxNameLength, "First name");
        CheckLength(problems, surname, MaxSurnameLength, "Surname");
        CheckLength(problems, phone, MaxPhoneLength, "Phone");
        CheckLength(problems, vatNumber, MaxVatNumberLength, "VAT number");
        CheckLength(problems, code, MaxCodeLength, "Vendor code");

        if (problems.Count > 0)
        {
            return problems;
        }

        var listed = await context.RouteCustomers
            .AsNoTracking()
            .Where(vendor => vendor.AssignedBusinessPartnerCode == cardCode && vendor.IsActive)
            .ToListAsync(cancellationToken);

        var same = listed.FirstOrDefault(vendor =>
            SameText(vendor.Name, name) &&
            SameText(vendor.Surname, surname) &&
            SamePhone(vendor.Phone, phone));

        if (same is not null)
        {
            return Errors.Vending.VendorAlreadyListed(same.Code, DisplayName(same.Name, same.Surname));
        }

        var created = await new CreateRouteCustomerHandler(context, notificationService, createLogger).Handle(
            new CreateRouteCustomerCommand(
                new CreateRouteCustomerRequest
                {
                    AssignedBusinessPartnerCode = cardCode,
                    Code = code,
                    Name = name!,
                    Surname = surname,
                    Phone = phone,
                    VatNumber = vatNumber,
                    IsActive = true
                },
                user.Id),
            cancellationToken);

        if (created.IsError)
        {
            return created.Errors;
        }

        logger.LogInformation(
            "Till account {UserId} added vendor {VendorCode} at {BusinessPartnerCode}",
            user.Id,
            created.Value.Code,
            cardCode);

        return new DesktopVendorDto
        {
            Code = created.Value.Code,
            Name = created.Value.Name,
            Surname = created.Value.Surname,
            Phone = created.Value.Phone,
            VatNumber = created.Value.VatNumber
        };
    }

    private static void CheckLength(List<Error> problems, string? value, int maxLength, string label)
    {
        if (value is not null && value.Length > maxLength)
        {
            problems.Add(Errors.Vending.FieldTooLong(label, maxLength));
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SameText(string? left, string? right) =>
        string.Equals(Clean(left), Clean(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Digits only, so <c>077 123 4567</c> and <c>0771234567</c> are one number. No phone on either side
    /// is a match: a retried request carries the same absence it did the first time.
    /// </summary>
    private static bool SamePhone(string? left, string? right) =>
        string.Equals(Digits(left), Digits(right), StringComparison.Ordinal);

    private static string Digits(string? value) =>
        value is null ? string.Empty : new string(value.Where(char.IsAsciiDigit).ToArray());

    private static string DisplayName(string name, string? surname) =>
        string.IsNullOrWhiteSpace(surname) ? name : $"{name} {surname}";
}
