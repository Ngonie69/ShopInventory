using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Queries.GetPasskeys;

public sealed class GetPasskeysHandler(
    ApplicationDbContext dbContext
) : IRequestHandler<GetPasskeysQuery, ErrorOr<List<PasskeyCredentialDto>>>
{
    public async Task<ErrorOr<List<PasskeyCredentialDto>>> Handle(
        GetPasskeysQuery request,
        CancellationToken cancellationToken)
    {
        var passkeys = await dbContext.PasskeyCredentials
            .AsNoTracking()
            .Where(pc => pc.UserId == request.UserId)
            .OrderByDescending(pc => pc.LastUsedAt ?? pc.CreatedAt)
            .Select(pc => new PasskeyCredentialDto
            {
                Id = pc.Id,
                FriendlyName = pc.FriendlyName,
                CreatedAt = pc.CreatedAt,
                LastUsedAt = pc.LastUsedAt
            })
            .ToListAsync(cancellationToken);

        return passkeys;
    }
}