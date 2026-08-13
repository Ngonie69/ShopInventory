using System.Text;
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

namespace ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;

public sealed class CreateRouteCustomerHandler(
    ApplicationDbContext context,
    INotificationService notificationService,
    ILogger<CreateRouteCustomerHandler> logger
) : IRequestHandler<CreateRouteCustomerCommand, ErrorOr<RouteCustomerDto>>
{
    public async Task<ErrorOr<RouteCustomerDto>> Handle(
        CreateRouteCustomerCommand command,
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

        var assignedBusinessPartnerCode = NullIfWhiteSpace(command.Request.AssignedBusinessPartnerCode)
            ?? NullIfWhiteSpace(user.AssignedBusinessPartnerCode);
        if (string.IsNullOrWhiteSpace(assignedBusinessPartnerCode))
        {
            return Errors.RouteCustomers.RouteBusinessPartnerRequired;
        }

        var name = NullIfWhiteSpace(command.Request.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Errors.RouteCustomers.NameRequired;
        }

        // The whole route, not just its codes: a removed customer keeps its row, and re-adding that
        // shop has to reuse it rather than fork a second one. Tens of rows per route.
        var routeCustomers = await context.RouteCustomers
            .Include(customer => customer.CreatedByUser)
            .Where(customer => customer.AssignedBusinessPartnerCode == assignedBusinessPartnerCode)
            .ToListAsync(cancellationToken);

        var requestedCode = NormalizeCode(command.Request.Code);
        var baseCode = requestedCode ?? NormalizeCode(name) ?? DefaultCode;

        // A shop that was removed and is being captured again. Reactivating its row is what keeps
        // its trading history attached to the shop that is trading again — a new row would leave the
        // old takings stranded on a customer nobody can see, and would collide on the unique
        // (route, code) index anyway.
        var removed = routeCustomers.FirstOrDefault(candidate => !candidate.IsActive
            && string.Equals(candidate.Code, baseCode, StringComparison.OrdinalIgnoreCase));

        if (removed is not null)
        {
            return await RestoreAsync(removed, command, name, cancellationToken);
        }

        var existingCodes = routeCustomers.Select(customer => customer.Code).ToList();

        // An explicit code that is taken is still an error. Only a generated one may take a suffix,
        // because the caller did not ask for that particular code in the first place.
        var code = requestedCode ?? GenerateCode(name, existingCodes);

        if (existingCodes.Any(existingCode => string.Equals(existingCode, code, StringComparison.OrdinalIgnoreCase)))
        {
            return Errors.RouteCustomers.CodeAlreadyExists(assignedBusinessPartnerCode, code);
        }

        var entity = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = assignedBusinessPartnerCode,
            Code = code,
            Name = name,
            Phone = NullIfWhiteSpace(command.Request.Phone),
            Email = NullIfWhiteSpace(command.Request.Email),
            Address = NullIfWhiteSpace(command.Request.Address),
            VatNumber = NullIfWhiteSpace(command.Request.VatNumber),
            IsActive = command.Request.IsActive,
            CreatedByUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };

        context.RouteCustomers.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        await NotifyRouteUsersAsync(entity, cancellationToken);

        return Map(entity, user.Username);
    }

    /// <summary>
    /// Brings a removed customer back under the details it has just been captured with.
    ///
    /// <c>CreatedAt</c> and <c>CreatedByUserId</c> are left alone: they record when this shop was
    /// first put on the route, which is what the reports date its trading from, and it was not
    /// today. <c>UpdatedAt</c> carries the fact that something happened.
    /// </summary>
    private async Task<ErrorOr<RouteCustomerDto>> RestoreAsync(
        RouteCustomerEntity removed,
        CreateRouteCustomerCommand command,
        string name,
        CancellationToken cancellationToken)
    {
        removed.Name = name;
        removed.Phone = NullIfWhiteSpace(command.Request.Phone);
        removed.Email = NullIfWhiteSpace(command.Request.Email);
        removed.Address = NullIfWhiteSpace(command.Request.Address);
        removed.VatNumber = NullIfWhiteSpace(command.Request.VatNumber);
        removed.IsActive = command.Request.IsActive;
        removed.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        // The route's handsets see it appear, same as any capture — from where they stand it is one.
        await NotifyRouteUsersAsync(removed, cancellationToken);

        return Map(removed, removed.CreatedByUser?.Username);
    }

    private async Task NotifyRouteUsersAsync(RouteCustomerEntity entity, CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await context.Users
                .AsNoTracking()
                .Where(candidate => candidate.IsActive
                    && candidate.AssignedBusinessPartnerCode == entity.AssignedBusinessPartnerCode
                    && (candidate.Role == ApplicationRoles.Adr || candidate.Role == ApplicationRoles.Sales)
                    && candidate.Username != null
                    && candidate.Username != string.Empty)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.Username
                })
                .ToListAsync(cancellationToken);

            foreach (var recipient in recipients
                         .GroupBy(candidate => candidate.Username!, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                var notification = WorkflowNotificationFactory.CreateRouteCustomerCreatedNotification(
                    recipient.Id,
                    recipient.Username!,
                    entity.AssignedBusinessPartnerCode,
                    entity.Id,
                    entity.Code,
                    entity.Name);

                await notificationService.CreateNotificationAsync(notification, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to publish route customer notifications for route customer {RouteCustomerId}",
                entity.Id);
        }
    }

    private static RouteCustomerDto Map(RouteCustomerEntity entity, string? createdByUserName)
    {
        return new RouteCustomerDto
        {
            Id = entity.Id,
            AssignedBusinessPartnerCode = entity.AssignedBusinessPartnerCode,
            Code = entity.Code,
            Name = entity.Name,
            Phone = entity.Phone,
            Email = entity.Email,
            Address = entity.Address,
            VatNumber = entity.VatNumber,
            IsActive = entity.IsActive,
            CreatedByUserId = entity.CreatedByUserId,
            CreatedByUserName = createdByUserName,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            if (character is '-' or '_')
            {
                builder.Append('-');
            }
        }

        var normalized = builder.ToString().Trim('-');
        if (normalized.Length > 50)
        {
            normalized = normalized[..50];
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private const string DefaultCode = "ROUTE-CUSTOMER";

    private static string GenerateCode(string name, IReadOnlyCollection<string> existingCodes)
    {
        var baseCode = NormalizeCode(name) ?? DefaultCode;
        var candidate = baseCode;
        var suffix = 2;

        while (existingCodes.Any(existingCode => string.Equals(existingCode, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            var suffixToken = $"-{suffix}";
            var prefixLength = Math.Max(1, 50 - suffixToken.Length);
            candidate = $"{baseCode[..Math.Min(baseCode.Length, prefixLength)]}{suffixToken}";
            suffix++;
        }

        return candidate;
    }
}
