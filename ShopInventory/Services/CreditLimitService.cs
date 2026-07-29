using System.Globalization;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Decides whether a sales order would put a customer over its credit limit.
/// </summary>
/// <remarks>
/// Mirrors the approval query SAP runs on ORDR:
/// <c>(DocTotal + OCRD.Balance) &gt; OCRD.CreditLine</c>, with three deliberate differences. Open
/// sales orders count toward exposure as well, because SAP's balance only holds what has been
/// invoiced and two orders raised minutes apart would otherwise each pass on their own. Where an
/// account names a consolidating parent (OCRD.FatherCard), the parent's limit is measured against
/// the whole group's exposure, since that is where the limit is actually held. And the SAP query's
/// <c>IndustryC = '3'</c> filter is not reproduced: the check applies wherever a limit is actually
/// set, so an account that was never flagged into that industry is still held to its own limit.
/// </remarks>
public interface ICreditLimitService
{
    /// <summary>
    /// Checks a prospective sales order against the customer's credit limit and, where one exists,
    /// its parent account's. Never throws for SAP problems — a lookup failure returns an allowed
    /// result so an outage cannot stop order capture.
    /// </summary>
    Task<CreditLimitCheckResult> CheckSalesOrderAsync(
        string? cardCode,
        decimal docTotal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a credit check. <see cref="Message"/> is written for a sales rep and is safe to
/// return to the caller verbatim.
/// </summary>
public sealed class CreditLimitCheckResult
{
    public required bool IsWithinLimit { get; init; }
    public string? Message { get; init; }

    /// <summary>The account whose limit was measured — the parent, for a consolidated group.</summary>
    public string? CreditAccountCardCode { get; init; }

    public decimal CreditLimit { get; init; }

    /// <summary>Balance + open orders + this order, in the credit account's currency.</summary>
    public decimal Exposure { get; init; }

    public static CreditLimitCheckResult Allowed() => new() { IsWithinLimit = true };
}

/// <summary>
/// Thrown when an order is refused on credit. Derives from <see cref="InvalidOperationException"/>
/// so the sales order handlers already surface it as a 400 with the message intact, rather than as
/// an unexpected server error.
/// </summary>
public sealed class CreditLimitExceededException : InvalidOperationException
{
    public CreditLimitExceededException(string message) : base(message)
    {
    }
}

public sealed class CreditLimitService : ICreditLimitService
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<CreditLimitService> _logger;
    private readonly CreditLimitSettings _settings;

    // Scoped to the request, not time-based. A web order is checked once before it is created and
    // again before it is posted, moments apart, and re-reading SAP for the second is waste; but the
    // *next* order must see the balance as it stands then, so nothing survives the scope.
    private readonly Dictionary<string, BusinessPartnerCreditProfileDto?> _profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BusinessPartnerCreditProfileDto>> _groups =
        new(StringComparer.OrdinalIgnoreCase);

    public CreditLimitService(
        ISAPServiceLayerClient sapClient,
        ILogger<CreditLimitService> logger,
        IOptions<CreditLimitSettings> settings)
    {
        _sapClient = sapClient;
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task<CreditLimitCheckResult> CheckSalesOrderAsync(
        string? cardCode,
        decimal docTotal,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(cardCode))
        {
            return CreditLimitCheckResult.Allowed();
        }

        var normalizedCardCode = cardCode.Trim();

        BusinessPartnerCreditProfileDto? account;
        try
        {
            account = await GetProfileAsync(normalizedCardCode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Selling continues through a SAP outage. SAP's own approval procedure still catches the
            // breach when the document reaches it, so this degrades rather than blocks.
            _logger.LogWarning(
                ex,
                "Could not read the credit profile for {CardCode} from SAP; the credit limit was not enforced for this order",
                normalizedCardCode);
            return CreditLimitCheckResult.Allowed();
        }

        if (account == null)
        {
            _logger.LogWarning(
                "Business partner {CardCode} was not found in SAP during the credit check; the credit limit was not enforced for this order",
                normalizedCardCode);
            return CreditLimitCheckResult.Allowed();
        }

        // The group is measured first: where an account draws its limit from a parent, that is the
        // limit that matters, and its message names the account a rep would have to chase.
        var groupResult = await CheckConsolidatedGroupAsync(account, docTotal, cancellationToken);
        if (groupResult is { IsWithinLimit: false })
        {
            return groupResult;
        }

        return CheckSingleAccount(account, docTotal);
    }

    /// <summary>
    /// Measures the whole consolidated group against the parent's limit. Returns null when the
    /// account has no parent, or the parent holds no limit of its own to measure.
    /// </summary>
    private async Task<CreditLimitCheckResult?> CheckConsolidatedGroupAsync(
        BusinessPartnerCreditProfileDto account,
        decimal docTotal,
        CancellationToken cancellationToken)
    {
        var parentCardCode = account.FatherCard?.Trim();
        if (string.IsNullOrWhiteSpace(parentCardCode) ||
            string.Equals(parentCardCode, account.CardCode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        List<BusinessPartnerCreditProfileDto> group;
        try
        {
            group = await GetGroupAsync(parentCardCode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not read the accounts consolidated under {ParentCardCode} from SAP; the group credit limit was not enforced for an order on {CardCode}",
                parentCardCode,
                account.CardCode);
            return null;
        }

        var parent = group.FirstOrDefault(p =>
            string.Equals(p.CardCode, parentCardCode, StringComparison.OrdinalIgnoreCase));

        if (parent == null)
        {
            _logger.LogWarning(
                "Business partner {CardCode} names {ParentCardCode} as its consolidating account but SAP has no such partner; the group credit limit was not enforced",
                account.CardCode,
                parentCardCode);
            return null;
        }

        if (parent.CreditLimit <= 0)
        {
            return null;
        }

        var balance = group.Sum(p => p.Balance);
        var openOrders = _settings.IncludeOpenOrders ? group.Sum(p => p.OpenOrdersBalance) : 0m;
        var exposure = balance + openOrders + docTotal;

        if (exposure <= parent.CreditLimit)
        {
            return CreditLimitCheckResult.Allowed();
        }

        _logger.LogWarning(
            "Refused a {DocTotal} sales order on {CardCode}: the group under {ParentCardCode} would reach {Exposure} against a {CreditLimit} limit across {AccountCount} accounts",
            docTotal,
            account.CardCode,
            parent.CardCode,
            exposure,
            parent.CreditLimit,
            group.Count);

        return new CreditLimitCheckResult
        {
            IsWithinLimit = false,
            CreditAccountCardCode = parent.CardCode,
            CreditLimit = parent.CreditLimit,
            Exposure = exposure,
            Message = BuildGroupMessage(account, parent, group.Count, balance, openOrders, docTotal, exposure)
        };
    }

    private CreditLimitCheckResult CheckSingleAccount(BusinessPartnerCreditProfileDto account, decimal docTotal)
    {
        if (account.CreditLimit <= 0)
        {
            return CreditLimitCheckResult.Allowed();
        }

        var openOrders = _settings.IncludeOpenOrders ? account.OpenOrdersBalance : 0m;
        var exposure = account.Balance + openOrders + docTotal;

        if (exposure <= account.CreditLimit)
        {
            return CreditLimitCheckResult.Allowed();
        }

        _logger.LogWarning(
            "Refused a {DocTotal} sales order on {CardCode}: the account would reach {Exposure} against a {CreditLimit} limit",
            docTotal,
            account.CardCode,
            exposure,
            account.CreditLimit);

        return new CreditLimitCheckResult
        {
            IsWithinLimit = false,
            CreditAccountCardCode = account.CardCode,
            CreditLimit = account.CreditLimit,
            Exposure = exposure,
            Message = BuildAccountMessage(account, account.Balance, openOrders, docTotal, exposure)
        };
    }

    private async Task<BusinessPartnerCreditProfileDto?> GetProfileAsync(
        string cardCode,
        CancellationToken cancellationToken)
    {
        if (_profiles.TryGetValue(cardCode, out var cached))
        {
            return cached;
        }

        var profile = await _sapClient.GetBusinessPartnerCreditProfileAsync(cardCode, cancellationToken);
        _profiles[cardCode] = profile;
        return profile;
    }

    private async Task<List<BusinessPartnerCreditProfileDto>> GetGroupAsync(
        string parentCardCode,
        CancellationToken cancellationToken)
    {
        if (_groups.TryGetValue(parentCardCode, out var cached))
        {
            return cached;
        }

        var group = await _sapClient.GetConsolidatedCreditProfilesAsync(parentCardCode, cancellationToken);
        _groups[parentCardCode] = group;
        return group;
    }

    private string BuildAccountMessage(
        BusinessPartnerCreditProfileDto account,
        decimal balance,
        decimal openOrders,
        decimal docTotal,
        decimal exposure)
    {
        var currency = FormatCurrency(account.Currency);

        var message =
            $"This order would take {Describe(account)} over its credit limit. " +
            $"Credit limit {Money(account.CreditLimit, currency)}, " +
            $"current balance {Money(balance, currency)}";

        if (_settings.IncludeOpenOrders)
        {
            message += $", open orders {Money(openOrders, currency)}";
        }

        return message +
            $", this order {Money(docTotal, currency)} — " +
            $"{Money(exposure - account.CreditLimit, currency)} over. " +
            "Take a payment against the account or reduce the order before submitting it again.";
    }

    private string BuildGroupMessage(
        BusinessPartnerCreditProfileDto account,
        BusinessPartnerCreditProfileDto parent,
        int accountCount,
        decimal balance,
        decimal openOrders,
        decimal docTotal,
        decimal exposure)
    {
        var currency = FormatCurrency(parent.Currency ?? account.Currency);

        var message =
            $"This order would take the group under {Describe(parent)} over its credit limit. " +
            $"{Describe(account)} draws its limit from that account. " +
            $"Group credit limit {Money(parent.CreditLimit, currency)}, " +
            $"combined balance across {accountCount} account{(accountCount == 1 ? "" : "s")} {Money(balance, currency)}";

        if (_settings.IncludeOpenOrders)
        {
            message += $", open orders {Money(openOrders, currency)}";
        }

        return message +
            $", this order {Money(docTotal, currency)} — " +
            $"{Money(exposure - parent.CreditLimit, currency)} over. " +
            "Take a payment against the group or reduce the order before submitting it again.";
    }

    private static string Describe(BusinessPartnerCreditProfileDto account) =>
        string.IsNullOrWhiteSpace(account.CardName)
            ? account.CardCode
            : $"{account.CardName} ({account.CardCode})";

    private static string FormatCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? string.Empty : currency.Trim() + " ";

    private static string Money(decimal value, string currencyPrefix) =>
        currencyPrefix + value.ToString("N2", CultureInfo.InvariantCulture);
}
