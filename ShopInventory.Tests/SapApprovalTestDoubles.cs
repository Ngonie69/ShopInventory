using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Data;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>The lookups the credit note approval handlers name people and stages through, answered from dictionaries.</summary>
internal sealed class FakeSapApprovalLookups : ISapApprovalLookups
{
    public Dictionary<int, SAPUser> Users { get; } = [];
    public Dictionary<int, SAPApprovalStage> Stages { get; } = [];
    public Dictionary<int, SAPApprovalTemplate> Templates { get; } = [];

    public string ServiceApproverUserCode { get; init; } = "manager";

    /// <summary>Stand in for a Service Layer that is not answering: every lookup throws.</summary>
    public bool Unavailable { get; set; }

    public Task<SAPUser?> GetServiceApproverAsync(CancellationToken cancellationToken)
        => Answer(Users.Values.FirstOrDefault(user => string.Equals(user.UserCode, ServiceApproverUserCode, StringComparison.OrdinalIgnoreCase)));

    public Task<SAPUser?> GetUserAsync(int internalKey, CancellationToken cancellationToken)
        => Answer(Users.GetValueOrDefault(internalKey));

    public Task<SAPApprovalTemplate?> GetTemplateAsync(int code, CancellationToken cancellationToken)
        => Answer(Templates.GetValueOrDefault(code));

    public Task<SAPApprovalStage?> GetStageAsync(int code, CancellationToken cancellationToken)
        => Answer(Stages.GetValueOrDefault(code));

    /// <summary>A stage listing these approver keys, plus the users named and template 7 "Returns".</summary>
    public static FakeSapApprovalLookups WithStage(int code, string name, IEnumerable<SAPUser> users, params int[] approvers)
    {
        var lookups = new FakeSapApprovalLookups();
        lookups.Stages[code] = new SAPApprovalStage
        {
            Code = code,
            Name = name,
            NoOfApproversRequired = 1,
            ApprovalStageApprovers = approvers.Select(id => new SAPApprovalStageApprover { UserID = id }).ToList()
        };
        lookups.Templates[7] = new SAPApprovalTemplate { Code = 7, Name = "Returns", IsActive = SapYesNo.Yes };
        foreach (var user in users)
        {
            lookups.Users[user.InternalKey] = user;
        }

        return lookups;
    }

    private Task<T?> Answer<T>(T? value) where T : class
        => Unavailable ? throw new HttpRequestException("SAP is not answering") : Task.FromResult(value);
}

/// <summary>Keeps every audit entry so a test can say what was recorded and whether it was a success.</summary>
internal sealed class RecordingAuditService : IAuditService
{
    public List<(string Action, string? EntityId, bool Success, string? Details, string? Error)> Entries { get; } = [];

    public Task LogAsync(string action, string username, string userRole, string? entityType = null,
        string? entityId = null, string? details = null, string? endpoint = null,
        bool isSuccess = true, string? errorMessage = null)
    {
        Entries.Add((action, entityId, isSuccess, details, errorMessage));
        return Task.CompletedTask;
    }

    public Task LogAsync(string action, string? entityType = null, string? entityId = null)
    {
        Entries.Add((action, entityId, true, null, null));
        return Task.CompletedTask;
    }

    public Task LogAsync(string action, string? entityType, string? entityId, string? details,
        bool isSuccess, string? errorMessage = null)
    {
        Entries.Add((action, entityId, isSuccess, details, errorMessage));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hands the real idempotency store a fresh context over one SQLite connection per scope, the way it
/// runs in production — the durable claim is the thing under test, so a fake would prove nothing.
/// </summary>
internal sealed class SingleDbContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
    : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    public IServiceScope CreateScope() => this;

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType)
        => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;

    public void Dispose()
    {
    }
}
