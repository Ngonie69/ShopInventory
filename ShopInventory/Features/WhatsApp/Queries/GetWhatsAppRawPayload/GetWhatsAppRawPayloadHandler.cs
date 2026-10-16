using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.WhatsApp.Queries.GetWhatsAppRawPayload;

/// <remarks>
/// The inbox list leaves <c>RawPayload</c> out, because a received attachment is kept in it whole
/// as base64 and a page of 50 could carry tens of MB. This reads one row's, when it is asked for.
/// </remarks>
public sealed class GetWhatsAppRawPayloadHandler(ApplicationDbContext dbContext) : IRequestHandler<GetWhatsAppRawPayloadQuery, ErrorOr<string>>
{
    private readonly ApplicationDbContext _dbContext = dbContext;

    public async Task<ErrorOr<string>> Handle(GetWhatsAppRawPayloadQuery query, CancellationToken cancellationToken)
    {
        var rawPayload = await _dbContext.WhatsAppWebhookEvents
            .AsNoTracking()
            .Where(message => message.Id == query.Id)
            .Select(message => message.RawPayload)
            .FirstOrDefaultAsync(cancellationToken);

        return rawPayload is null ? Errors.WhatsApp.MessageNotFound(query.Id) : rawPayload;
    }
}
