using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Sweeps the whole customer base for accounts and consolidated groups that are currently sitting
/// over their credit limit, so they can be chased before the next day's orders are refused.
/// </summary>
/// <remarks>
/// Same arithmetic as the per-order check in <see cref="ICreditLimitService"/>, minus the order:
/// this measures what is already owed. Deliberately a separate read — the order path looks up one
/// account live, this reads every customer once and does the grouping in memory, which is one SAP
/// sweep instead of thousands of round trips.
/// </remarks>
public interface ICreditLimitReviewService
{
    Task<CreditLimitReview> ReviewAsync(CancellationToken cancellationToken = default);
}

public sealed class CreditLimitReview
{
    /// <summary>Accounts and groups over their limit, worst first.</summary>
    public IReadOnlyList<CreditLimitBreach> Breaches { get; init; } = [];

    /// <summary>Customers read from SAP.</summary>
    public int CustomersRead { get; init; }

    /// <summary>Accounts and groups that actually hold a limit, so had something to measure.</summary>
    public int LimitsMeasured { get; init; }

    public decimal TotalOver => Breaches.Sum(breach => breach.AmountOver);
}

public sealed class CreditLimitBreach
{
    public required string CardCode { get; init; }
    public string? CardName { get; init; }
    public string? Currency { get; init; }

    /// <summary>True when this is a consolidated group measured against the parent's limit.</summary>
    public bool IsGroup { get; init; }

    /// <summary>Accounts included — 1 for a standalone account.</summary>
    public int AccountCount { get; init; }

    public decimal CreditLimit { get; init; }
    public decimal Balance { get; init; }
    public decimal OpenOrders { get; init; }
    public decimal Exposure { get; init; }

    public decimal AmountOver => Exposure - CreditLimit;

    public string Describe() =>
        string.IsNullOrWhiteSpace(CardName) ? CardCode : $"{CardName} ({CardCode})";
}

public sealed class CreditLimitReviewService : ICreditLimitReviewService
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<CreditLimitReviewService> _logger;
    private readonly CreditLimitSettings _settings;

    public CreditLimitReviewService(
        ISAPServiceLayerClient sapClient,
        ILogger<CreditLimitReviewService> logger,
        IOptions<CreditLimitSettings> settings)
    {
        _sapClient = sapClient;
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task<CreditLimitReview> ReviewAsync(CancellationToken cancellationToken = default)
    {
        var customers = await _sapClient.GetCustomerCreditProfilesAsync(cancellationToken);

        var breaches = new List<CreditLimitBreach>();
        var limitsMeasured = 0;

        var byCardCode = customers
            .GroupBy(customer => customer.CardCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var childrenByParent = customers
            .Where(customer => !string.IsNullOrWhiteSpace(customer.FatherCard)
                && !string.Equals(customer.FatherCard, customer.CardCode, StringComparison.OrdinalIgnoreCase))
            .GroupBy(customer => customer.FatherCard!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        // Accounts already answered for by a breached group, so they are not reported twice.
        var coveredByGroup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (parentCardCode, children) in childrenByParent)
        {
            if (!byCardCode.TryGetValue(parentCardCode, out var parent))
            {
                _logger.LogWarning(
                    "{ChildCount} customer account(s) name {ParentCardCode} as their consolidating account but SAP has no such customer; their group limit cannot be measured",
                    children.Count,
                    parentCardCode);
                continue;
            }

            if (parent.CreditLimit <= 0)
            {
                // No group limit to measure. The children still get measured on their own below.
                continue;
            }

            limitsMeasured++;

            var group = children.Append(parent).ToList();
            var balance = group.Sum(account => account.Balance);
            var openOrders = _settings.IncludeOpenOrders ? group.Sum(account => account.OpenOrdersBalance) : 0m;
            var exposure = balance + openOrders;

            if (exposure <= parent.CreditLimit)
            {
                continue;
            }

            foreach (var account in group)
            {
                coveredByGroup.Add(account.CardCode);
            }

            breaches.Add(new CreditLimitBreach
            {
                CardCode = parent.CardCode,
                CardName = parent.CardName,
                Currency = parent.Currency,
                IsGroup = true,
                AccountCount = group.Count,
                CreditLimit = parent.CreditLimit,
                Balance = balance,
                OpenOrders = openOrders,
                Exposure = exposure
            });
        }

        foreach (var account in customers)
        {
            if (account.CreditLimit <= 0 || coveredByGroup.Contains(account.CardCode))
            {
                continue;
            }

            limitsMeasured++;

            var openOrders = _settings.IncludeOpenOrders ? account.OpenOrdersBalance : 0m;
            var exposure = account.Balance + openOrders;

            if (exposure <= account.CreditLimit)
            {
                continue;
            }

            breaches.Add(new CreditLimitBreach
            {
                CardCode = account.CardCode,
                CardName = account.CardName,
                Currency = account.Currency,
                IsGroup = false,
                AccountCount = 1,
                CreditLimit = account.CreditLimit,
                Balance = account.Balance,
                OpenOrders = openOrders,
                Exposure = exposure
            });
        }

        return new CreditLimitReview
        {
            Breaches = breaches.OrderByDescending(breach => breach.AmountOver).ToList(),
            CustomersRead = customers.Count,
            LimitsMeasured = limitsMeasured
        };
    }

    /// <summary>
    /// One-line summary for a notification title.
    /// </summary>
    public static string BuildSummary(CreditLimitReview review)
    {
        var accountWord = review.Breaches.Count == 1 ? "account is" : "accounts are";
        return $"{review.Breaches.Count} {accountWord} over the credit limit";
    }

    /// <summary>
    /// Notification body: the worst offenders in full, then a count of the rest. A bell listing
    /// forty accounts is not read by anyone — the complete list goes to the log.
    /// </summary>
    public static string BuildMessage(CreditLimitReview review, int maxListed)
    {
        var message = new StringBuilder();
        var listed = review.Breaches.Take(Math.Max(1, maxListed)).ToList();

        foreach (var breach in listed)
        {
            var currency = string.IsNullOrWhiteSpace(breach.Currency) ? string.Empty : breach.Currency.Trim() + " ";
            message.Append(breach.IsGroup
                ? $"Group under {breach.Describe()} ({breach.AccountCount} accounts): "
                : $"{breach.Describe()}: ");
            message.Append(
                $"{Money(breach.Exposure, currency)} against a {Money(breach.CreditLimit, currency)} limit — " +
                $"{Money(breach.AmountOver, currency)} over. ");
        }

        var remaining = review.Breaches.Count - listed.Count;
        if (remaining > 0)
        {
            message.Append($"And {remaining} more. ");
        }

        message.Append("Orders for these accounts will be refused until the balance is brought back under the limit.");
        return message.ToString();
    }

    private static string Money(decimal value, string currencyPrefix) =>
        currencyPrefix + value.ToString("N2", CultureInfo.InvariantCulture);
}
