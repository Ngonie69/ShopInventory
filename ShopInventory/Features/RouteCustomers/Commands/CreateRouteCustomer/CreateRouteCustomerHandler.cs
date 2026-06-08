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

        var existingCodes = await context.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.AssignedBusinessPartnerCode == assignedBusinessPartnerCode)
            .Select(customer => customer.Code)
            .ToListAsync(cancellationToken);

        var requestedCode = NormalizeCode(command.Request.Code);
        var code = string.IsNullOrWhiteSpace(requestedCode)
            ? GenerateCode(name, existingCodes)
            : requestedCode;

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

    private static string GenerateCode(string name, IReadOnlyCollection<string> existingCodes)
    {
        var baseCode = NormalizeCode(name) ?? "ROUTE-CUSTOMER";
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
