using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Extensions;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;
using BC = BCrypt.Net.BCrypt;

namespace ShopInventory.Features.Users.Commands.CreateUser;

public sealed class CreateUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<CreateUserHandler> logger
) : IRequestHandler<CreateUserCommand, ErrorOr<UserCreatedResponseDto>>
{
    private static readonly string[] ValidRoles = { "Admin", "Manager", "Cashier", "StockController", "DepotController", "PodOperator", "Driver", "Merchandiser", "SalesRep", "MerchandiserPurchaseOrderViewer" };

    public async Task<ErrorOr<UserCreatedResponseDto>> Handle(
        CreateUserCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Errors.User.CreationFailed("Username and password are required");
        }

        if (await context.Users.WhereUsernameMatches(request.Username).AnyAsync(cancellationToken))
        {
            return Errors.User.DuplicateUsername;
        }

        if (!string.IsNullOrEmpty(request.Email) && await context.Users.WhereEmailMatches(request.Email).AnyAsync(cancellationToken))
        {
            return Errors.User.CreationFailed("Email already exists");
        }

        if (!ValidRoles.Contains(request.Role))
        {
            return Errors.User.CreationFailed($"Invalid role. Valid roles are: {string.Join(", ", ValidRoles)}");
        }

        if ((request.Role == "StockController" || request.Role == "DepotController") && (request.AssignedWarehouseCodes == null || request.AssignedWarehouseCodes.Count == 0))
        {
            return Errors.User.CreationFailed($"At least one assigned warehouse code is required for {request.Role} role");
        }

        if (request.Role == "Merchandiser" && (request.AssignedCustomerCodes == null || request.AssignedCustomerCodes.Count == 0))
        {
            return Errors.User.CreationFailed("At least one assigned customer code is required for Merchandiser role");
        }

        if (request.Role == "Driver" && string.IsNullOrWhiteSpace(request.AssignedSection))
        {
            return Errors.User.CreationFailed("An assigned section is required for Driver role");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            PasswordHash = BC.HashPassword(request.Password),
            Email = request.Email,
            Role = request.Role,
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        if (request.Role == "StockController" || request.Role == "DepotController")
            user.SetWarehouseCodes(request.AssignedWarehouseCodes);

        if (request.Role == "Merchandiser")
            user.SetCustomerCodes(request.AssignedCustomerCodes);

        if (request.Role == "Driver")
            user.AssignedSection = request.AssignedSection;

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} created by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.CreateUser, "User", user.Id.ToString(), $"User {user.Username} created with role {user.Role}", true); } catch { }

        return new UserCreatedResponseDto
        {
            Message = "User created successfully",
            User = new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                Role = user.Role,
                FirstName = user.FirstName,
                LastName = user.LastName,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                AssignedWarehouseCodes = user.GetWarehouseCodes(),
                AssignedCustomerCodes = user.GetCustomerCodes(),
                AssignedSection = user.AssignedSection
            }
        };
    }
}
