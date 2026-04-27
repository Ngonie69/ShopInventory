using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserManagement.Queries.GetManagedMerchandiserAccounts;

public sealed class GetManagedMerchandiserAccountsHandler(
    ApplicationDbContext context
) : IRequestHandler<GetManagedMerchandiserAccountsQuery, ErrorOr<List<ManagedMerchandiserAccountDto>>>
{
    public async Task<ErrorOr<List<ManagedMerchandiserAccountDto>>> Handle(
        GetManagedMerchandiserAccountsQuery query,
        CancellationToken cancellationToken)
    {
        var merchandisers = await context.Users
            .AsNoTracking()
            .Where(user => user.Role == "Merchandiser")
            .OrderBy(user => user.Username)
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.Email,
                user.FirstName,
                user.LastName,
                user.IsActive,
                user.CreatedAt,
                user.AssignedWarehouseCodes,
                user.AssignedCustomerCodes
            })
            .ToListAsync(cancellationToken);

        return merchandisers
            .Select(merchandiser => new ManagedMerchandiserAccountDto
            {
                Id = merchandiser.Id,
                Username = merchandiser.Username,
                Email = merchandiser.Email,
                FirstName = merchandiser.FirstName,
                LastName = merchandiser.LastName,
                IsActive = merchandiser.IsActive,
                CreatedAt = merchandiser.CreatedAt,
                AssignedWarehouseCodes = DeserializeCodes(merchandiser.AssignedWarehouseCodes),
                AssignedCustomerCodes = DeserializeCodes(merchandiser.AssignedCustomerCodes)
            })
            .ToList();
    }

    private static List<string> DeserializeCodes(string? rawCodes)
    {
        if (string.IsNullOrWhiteSpace(rawCodes))
        {
            return new List<string>();
        }

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(rawCodes) ?? new List<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}