using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.WhatsApp.Queries.GetWhatsAppInbox;

public sealed class GetWhatsAppInboxHandler(ApplicationDbContext dbContext) : IRequestHandler<GetWhatsAppInboxQuery, ErrorOr<WhatsAppInboxResponseDto>>
{
    private readonly ApplicationDbContext _dbContext = dbContext;

    public async Task<ErrorOr<WhatsAppInboxResponseDto>> Handle(GetWhatsAppInboxQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page <= 0 ? 1 : query.Page;
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var items = _dbContext.WhatsAppWebhookEvents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            items = items.Where(message =>
                EF.Functions.ILike(message.EventType, pattern)
                || (message.SessionName != null && EF.Functions.ILike(message.SessionName, pattern))
                || (message.MessageId != null && EF.Functions.ILike(message.MessageId, pattern))
                || (message.ChatId != null && EF.Functions.ILike(message.ChatId, pattern))
                || (message.SenderNumber != null && EF.Functions.ILike(message.SenderNumber, pattern))
                || (message.SenderDisplayName != null && EF.Functions.ILike(message.SenderDisplayName, pattern))
                || (message.TextBody != null && EF.Functions.ILike(message.TextBody, pattern)));
        }

        var totalCount = await items.CountAsync(cancellationToken);
        var messages = await items
            .OrderByDescending(message => message.OccurredAtUtc ?? message.ReceivedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(message => new WhatsAppInboxItemDto
            {
                Id = message.Id,
                EventType = message.EventType,
                SessionName = message.SessionName,
                MessageId = message.MessageId,
                ChatId = message.ChatId,
                SenderNumber = message.SenderNumber,
                SenderDisplayName = message.SenderDisplayName,
                MessageType = message.MessageType,
                Direction = message.Direction,
                Status = message.Status,
                IsFromMe = message.IsFromMe,
                TextBody = message.TextBody,
                SourcePath = message.SourcePath,
                OccurredAtUtc = message.OccurredAtUtc,
                ReceivedAtUtc = message.ReceivedAtUtc,
                RawPayload = message.RawPayload
            })
            .ToListAsync(cancellationToken);

        return new WhatsAppInboxResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Messages = messages
        };
    }
}