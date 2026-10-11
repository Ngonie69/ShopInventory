using System.Net;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Features.CustomerDocuments.Documents;
using ShopInventory.Features.Invoices;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// One in-memory database and the fakes the customer-document tests share: a gateway that records
/// what it is asked, a SAP that answers per method, a clock that only moves when told.
/// </summary>
internal sealed class CustomerDocumentTestKit : IDisposable
{
    public static readonly Guid Cashier = Guid.Parse("11111111-2222-3333-4444-555555555555");
    public const string SessionId = "documents-session";
    public const string CardCode = "SPA002";

    private readonly SqliteConnection _connection;

    public CustomerDocumentTestKit()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = NewContext();
        context.Database.EnsureCreated();
        context.Users.Add(new User
        {
            Id = Cashier,
            Username = "cashier",
            PasswordHash = "x",
            Role = "Cashier",
            FirstName = "Tendai",
            LastName = "Moyo",
            IsActive = true
        });
        context.SaveChanges();
    }

    public ApplicationDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    public void Dispose() => _connection.Dispose();

    public static CustomerDocumentDeliverySettings Settings(Action<CustomerDocumentDeliverySettings>? change = null)
    {
        var settings = new CustomerDocumentDeliverySettings
        {
            Enabled = true,
            MinSecondsBetweenSends = 6,
            JitterSeconds = 0,
            AutoWindowStartCat = "00:00",
            AutoWindowEndCat = "23:59",
            AutoSendOnSundays = true
        };
        change?.Invoke(settings);
        return settings;
    }

    public static OpenWASettings Gateway(bool configured = true) => new()
    {
        Enabled = configured,
        BaseUrl = "http://127.0.0.1:2785",
        ApiKey = configured ? "key" : string.Empty,
        TimeoutSeconds = 30,
        DocumentTimeoutSeconds = 60
    };

    /// <param name="stopped">
    /// What saving the settings with no session records: an administrator stopped all sending. A null
    /// <paramref name="sessionId"/> without it is a session nobody has chosen yet.
    /// </param>
    public async Task SetRuntimeAsync(bool autoSend = false, string? sessionId = SessionId, int maxAutoPerDay = 20, bool stopped = false)
    {
        await using var context = NewContext();
        context.SystemConfigs.AddRange(
            new SystemConfigEntity { Key = "CustomerDocuments.AutoSendEnabled", Value = autoSend ? "true" : "false" },
            new SystemConfigEntity { Key = "CustomerDocuments.WhatsAppSessionId", Value = sessionId },
            new SystemConfigEntity { Key = "CustomerDocuments.MaxAutoPerDay", Value = maxAutoPerDay.ToString() });
        if (stopped)
        {
            context.SystemConfigs.Add(new SystemConfigEntity { Key = "CustomerDocuments.SendingStopped", Value = "true" });
        }

        await context.SaveChangesAsync();
    }

    public async Task<string?> SavedSessionIdAsync()
    {
        await using var context = NewContext();
        return await context.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == "CustomerDocuments.WhatsAppSessionId")
            .Select(config => config.Value)
            .FirstOrDefaultAsync();
    }

    /// <summary>A number saved on a van's route customer rather than on a SAP card.</summary>
    public async Task<CustomerWhatsAppContactEntity> AddShopContactAsync(
        int routeCustomerId,
        string phone = "+263771234567",
        bool autoSend = true,
        DateTime? optedOutAtUtc = null)
    {
        await using var context = NewContext();
        var contact = new CustomerWhatsAppContactEntity
        {
            RouteCustomerId = routeCustomerId,
            OwnerName = "Mbare Tuck Shop",
            PhoneE164 = phone,
            AutoSendInvoices = autoSend,
            ConsentSource = WhatsAppConsentSource.Web,
            ConsentRecordedAtUtc = DateTime.UtcNow,
            ConsentRecordedBy = "Tendai Moyo",
            OptedOutAtUtc = optedOutAtUtc,
            WhatsAppExists = true,
            WhatsAppCheckedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        context.CustomerWhatsAppContacts.Add(contact);
        await context.SaveChangesAsync();
        return contact;
    }

    public async Task<CustomerWhatsAppContactEntity> AddContactAsync(
        string phone = "+263771234567",
        string cardCode = CardCode,
        bool autoSend = true,
        DateTime? optedOutAtUtc = null,
        bool? whatsAppExists = true)
    {
        await using var context = NewContext();
        var contact = new CustomerWhatsAppContactEntity
        {
            CardCode = cardCode,
            OwnerName = "Spar Bridge",
            PhoneE164 = phone,
            AutoSendInvoices = autoSend,
            ConsentSource = WhatsAppConsentSource.Web,
            ConsentRecordedAtUtc = DateTime.UtcNow,
            ConsentRecordedBy = "Tendai Moyo",
            OptedOutAtUtc = optedOutAtUtc,
            WhatsAppExists = whatsAppExists,
            WhatsAppCheckedAtUtc = whatsAppExists is null ? null : DateTime.UtcNow.AddDays(-1)
        };
        context.CustomerWhatsAppContacts.Add(contact);
        await context.SaveChangesAsync();
        return contact;
    }

    public async Task<CustomerDocumentDeliveryEntity> AddDeliveryAsync(Action<CustomerDocumentDeliveryEntity>? change = null)
    {
        await using var context = NewContext();
        var now = DateTime.UtcNow;
        var delivery = new CustomerDocumentDeliveryEntity
        {
            DocumentType = CustomerDocumentType.SapInvoice,
            SapDocEntry = 2400100,
            SapDocNum = 780100,
            DocumentNumber = "780100",
            DocumentDate = now.Date,
            DocumentTotal = 125.50m,
            Currency = "USD",
            CardCode = CardCode,
            CardName = "Spar Bridge",
            RecipientE164 = "+263771234567",
            Trigger = CustomerDocumentDeliveryTrigger.Manual,
            Priority = 1,
            Status = CustomerDocumentDeliveryStatus.Pending,
            NextAttemptAtUtc = now.AddMinutes(-1),
            CreatedAtUtc = now.AddMinutes(-1),
            UpdatedAtUtc = now.AddMinutes(-1),
            RequestedByUserId = Cashier,
            RequestedBy = "Tendai Moyo"
        };
        change?.Invoke(delivery);
        context.CustomerDocumentDeliveries.Add(delivery);
        await context.SaveChangesAsync();
        return delivery;
    }

    public async Task<CustomerDocumentDeliveryEntity> ReloadDeliveryAsync(long id)
    {
        await using var context = NewContext();
        return await context.CustomerDocumentDeliveries.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    public static ComposedCustomerDocument Document(string number = "780100") => new(
        Bytes: [0x25, 0x50, 0x44, 0x46],
        FileName: $"Kefalos-Invoice-{number}.pdf",
        Caption: $"Good day Spar Bridge. Please find attached Kefalos tax invoice {number}.",
        Sha256: "abc",
        Receipt: new InvoicePdfReceipt("https://fdms.zimra.co.zw/0000036189081020260000000123ABCD", "ABCD-1234-EF56-7890", "81", "36189", 123),
        FiscalEvidenceSource: FiscalLinkVerifier.TransactionLogSource);
}

/// <summary>An OpenWA that answers as told and remembers every call.</summary>
internal sealed class FakeOpenWAClient : IOpenWAClient
{
    public List<WhatsAppSendDocumentRequestDto> Documents { get; } = [];
    public List<string> NumberChecks { get; } = [];
    public string SessionStatus { get; set; } = "ready";
    public Func<WhatsAppSendDocumentRequestDto, Task<WhatsAppMessageDispatchDto>>? OnSend { get; set; }
    public Func<string, WhatsAppNumberCheckDto>? OnCheck { get; set; }
    public List<WhatsAppOutboundMessageDto> Log { get; } = [];
    public Exception? SessionsFailure { get; set; }

    /// <summary>What the gateway lists; null for the one documents session in <see cref="SessionStatus"/>.</summary>
    public List<WhatsAppSessionDto>? Sessions { get; set; }

    public Task<WhatsAppMessageDispatchDto> SendDocumentAsync(string sessionId, WhatsAppSendDocumentRequestDto request, CancellationToken cancellationToken = default)
    {
        Documents.Add(request);
        return OnSend?.Invoke(request)
            ?? Task.FromResult(new WhatsAppMessageDispatchDto { MessageId = $"true_{request.ChatId}_{Documents.Count}", Timestamp = 1 });
    }

    public Task<WhatsAppNumberCheckDto> CheckNumberAsync(string sessionId, string digits, CancellationToken cancellationToken = default)
    {
        NumberChecks.Add(digits);
        return Task.FromResult(OnCheck?.Invoke(digits) ?? new WhatsAppNumberCheckDto { Number = digits, Exists = true });
    }

    public Task<WhatsAppMessageHistoryDto> GetMessagesAsync(string sessionId, string chatId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WhatsAppMessageHistoryDto { Messages = Log.Where(message => message.ChatId == chatId).ToList(), Total = Log.Count });

    public Task<List<WhatsAppSessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
        SessionsFailure is not null
            ? Task.FromException<List<WhatsAppSessionDto>>(SessionsFailure)
            : Task.FromResult(Sessions ?? new List<WhatsAppSessionDto>
            {
                new() { Id = CustomerDocumentTestKit.SessionId, Name = "customer-documents", Status = SessionStatus }
            });

    public Task<WhatsAppHealthDto?> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppSessionDto> CreateSessionAsync(WhatsAppCreateSessionRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppSessionDto> StartSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppSessionDto> StopSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppQrCodeDto> GetSessionQrCodeAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppMessageDispatchDto> SendTextAsync(string sessionId, WhatsAppSendTextRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppMessageDispatchDto> ReplyAsync(string sessionId, WhatsAppReplyRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<List<WhatsAppWebhookRegistrationDto>> GetSessionWebhooksAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppWebhookRegistrationDto> CreateSessionWebhookAsync(string sessionId, WhatsAppWebhookRegistrationRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<WhatsAppWebhookRegistrationDto> UpdateSessionWebhookAsync(string sessionId, string webhookId, WhatsAppWebhookRegistrationRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public static OpenWAGatewayException Refusal(HttpStatusCode status, string message) =>
        new(status, status.ToString(), $"{{\"message\":\"{message}\",\"statusCode\":{(int)status}}}");
}

/// <summary>A document composer that answers whatever the test sets, and counts the calls.</summary>
internal sealed class FakeDocumentComposer : ISapInvoiceDocumentComposer
{
    public Func<CustomerDocumentDeliveryEntity, DocumentComposition> Answer { get; set; } =
        delivery => DocumentComposition.Ready(CustomerDocumentTestKit.Document(delivery.DocumentNumber));

    public int Calls { get; private set; }

    public Task<DocumentComposition> ComposeAsync(CustomerDocumentDeliveryEntity delivery, DateTime nowUtc, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Answer(delivery));
    }
}

/// <summary>A clock that stands still unless told, and records every wait instead of waiting.</summary>
internal sealed class FakePacer(DateTime startUtc) : ICustomerDocumentPacer
{
    public DateTime Now { get; set; } = startUtc;
    public List<TimeSpan> Delays { get; } = [];

    public DateTime UtcNow => Now;

    public int JitterSeconds(int maxSeconds) => 0;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        Now = Now.Add(delay);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAlerts : ICustomerDocumentAlerts
{
    public List<string> Raised { get; } = [];

    public Task RaiseAsync(string condition, string title, string message, CancellationToken cancellationToken)
    {
        Raised.Add(condition);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingDispatchTrigger : ICustomerDocumentDispatchTrigger
{
    public int Triggered { get; private set; }

    public Task TriggerAsync(CancellationToken cancellationToken)
    {
        Triggered++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => _stopping.Cancel();
}

/// <summary>
/// An <see cref="ISAPServiceLayerClient"/> that answers only the methods a test names, and throws on
/// any other — the interface is far too wide to implement for two calls.
/// </summary>
public class SapAnswers : DispatchProxy
{
    private Dictionary<string, Func<object?[]?, object?>> _answers = new();

    public static ISAPServiceLayerClient Create(Dictionary<string, Func<object?[]?, object?>> answers)
    {
        var proxy = Create<ISAPServiceLayerClient, SapAnswers>();
        ((SapAnswers)(object)proxy)._answers = answers;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is not null && _answers.TryGetValue(targetMethod.Name, out var answer))
        {
            return answer(args);
        }

        throw new InvalidOperationException($"{targetMethod?.Name} was not expected to be called.");
    }
}
