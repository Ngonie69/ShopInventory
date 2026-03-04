using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface ICustomerLinkedAccountService
{
    /// <summary>
    /// Get all linked accounts for a portal user by their login CardCode
    /// </summary>
    Task<List<LinkedAccountInfo>> GetLinkedAccountsAsync(string cardCode);

    /// <summary>
    /// Get the account structure type for a customer ("Single" or "Multi")
    /// </summary>
    Task<string> GetAccountStructureAsync(string cardCode);

    /// <summary>
    /// Get all CardCodes associated with a customer (for data aggregation).
    /// For single-account customers, returns just their CardCode.
    /// For multi-account customers, returns all main + sub CardCodes.
    /// </summary>
    Task<List<string>> GetAllCardCodesAsync(string cardCode);

    /// <summary>
    /// Get CardCodes for main accounts only (for invoices and payments)
    /// </summary>
    Task<List<string>> GetMainAccountCardCodesAsync(string cardCode);

    /// <summary>
    /// Get CardCodes for sub accounts only (for sales orders)
    /// </summary>
    Task<List<string>> GetSubAccountCardCodesAsync(string cardCode);

    /// <summary>
    /// Get linked accounts filtered by account type
    /// </summary>
    Task<List<LinkedAccountInfo>> GetLinkedAccountsByTypeAsync(string cardCode, string accountType);

    /// <summary>
    /// Set up a multi-account structure for a customer (admin operation)
    /// </summary>
    Task<LinkedAccountResponse> SetupMultiAccountAsync(SetupMultiAccountRequest request);

    /// <summary>
    /// Add a single linked account to a customer
    /// </summary>
    Task<LinkedAccountResponse> AddLinkedAccountAsync(string portalCardCode, LinkedAccountRequest request);

    /// <summary>
    /// Remove a linked account
    /// </summary>
    Task<LinkedAccountResponse> RemoveLinkedAccountAsync(string portalCardCode, string linkedCardCode);

    /// <summary>
    /// Convert a customer back to single-account structure (remove all linked accounts)
    /// </summary>
    Task<LinkedAccountResponse> ConvertToSingleAccountAsync(string portalCardCode);

    /// <summary>
    /// Check if a specific CardCode can perform a given transaction type
    /// </summary>
    Task<bool> CanPerformTransactionAsync(string portalCardCode, string targetCardCode, string transactionType);
}

public class CustomerLinkedAccountService : ICustomerLinkedAccountService
{
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly ILogger<CustomerLinkedAccountService> _logger;

    public CustomerLinkedAccountService(
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        ILogger<CustomerLinkedAccountService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<List<LinkedAccountInfo>> GetLinkedAccountsAsync(string cardCode)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var user = await db.CustomerPortalUsers
            .Include(u => u.LinkedAccounts)
            .FirstOrDefaultAsync(u => u.CardCode == cardCode);

        if (user == null || user.AccountStructure == "Single" || !user.LinkedAccounts.Any())
        {
            return new List<LinkedAccountInfo>();
        }

        return user.LinkedAccounts
            .Where(la => la.IsActive)
            .OrderBy(la => la.AccountType == "Main" ? 0 : 1) // Main accounts first
            .ThenBy(la => la.Currency)
            .ThenBy(la => la.CardCode)
            .Select(MapToLinkedAccountInfo)
            .ToList();
    }

    public async Task<string> GetAccountStructureAsync(string cardCode)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var user = await db.CustomerPortalUsers
            .FirstOrDefaultAsync(u => u.CardCode == cardCode);

        return user?.AccountStructure ?? "Single";
    }

    public async Task<List<string>> GetAllCardCodesAsync(string cardCode)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var user = await db.CustomerPortalUsers
            .Include(u => u.LinkedAccounts)
            .FirstOrDefaultAsync(u => u.CardCode == cardCode);

        if (user == null)
            return new List<string> { cardCode };

        if (user.AccountStructure == "Single" || !user.LinkedAccounts.Any())
            return new List<string> { cardCode };

        return user.LinkedAccounts
            .Where(la => la.IsActive)
            .Select(la => la.CardCode)
            .Distinct()
            .ToList();
    }

    public async Task<List<string>> GetMainAccountCardCodesAsync(string cardCode)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var user = await db.CustomerPortalUsers
            .Include(u => u.LinkedAccounts)
            .FirstOrDefaultAsync(u => u.CardCode == cardCode);

        if (user == null)
            return new List<string> { cardCode };

        if (user.AccountStructure == "Single" || !user.LinkedAccounts.Any())
            return new List<string> { cardCode };

        var mainCodes = user.LinkedAccounts
            .Where(la => la.IsActive && la.AccountType == "Main")
            .Select(la => la.CardCode)
            .Distinct()
            .ToList();

        return mainCodes.Any() ? mainCodes : new List<string> { cardCode };
    }

    public async Task<List<string>> GetSubAccountCardCodesAsync(string cardCode)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var user = await db.CustomerPortalUsers
            .Include(u => u.LinkedAccounts)
            .FirstOrDefaultAsync(u => u.CardCode == cardCode);

        if (user == null)
            return new List<string> { cardCode };

        if (user.AccountStructure == "Single" || !user.LinkedAccounts.Any())
            return new List<string> { cardCode };

        var subCodes = user.LinkedAccounts
            .Where(la => la.IsActive && la.AccountType == "Sub")
            .Select(la => la.CardCode)
            .Distinct()
            .ToList();

        return subCodes.Any() ? subCodes : new List<string>();
    }

    public async Task<List<LinkedAccountInfo>> GetLinkedAccountsByTypeAsync(string cardCode, string accountType)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var user = await db.CustomerPortalUsers
            .Include(u => u.LinkedAccounts)
            .FirstOrDefaultAsync(u => u.CardCode == cardCode);

        if (user == null || user.AccountStructure == "Single" || !user.LinkedAccounts.Any())
            return new List<LinkedAccountInfo>();

        return user.LinkedAccounts
            .Where(la => la.IsActive && la.AccountType == accountType)
            .OrderBy(la => la.Currency)
            .ThenBy(la => la.CardCode)
            .Select(MapToLinkedAccountInfo)
            .ToList();
    }

    public async Task<LinkedAccountResponse> SetupMultiAccountAsync(SetupMultiAccountRequest request)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var user = await db.CustomerPortalUsers
                .Include(u => u.LinkedAccounts)
                .FirstOrDefaultAsync(u => u.CardCode == request.PortalCardCode);

            if (user == null)
            {
                return new LinkedAccountResponse
                {
                    Success = false,
                    Message = $"Portal user with CardCode '{request.PortalCardCode}' not found"
                };
            }

            // Validate: must have at least one Main account
            if (!request.Accounts.Any(a => a.AccountType == "Main"))
            {
                return new LinkedAccountResponse
                {
                    Success = false,
                    Message = "At least one Main account is required for multi-account setup"
                };
            }

            // Validate: Sub accounts must reference a valid parent
            var mainCardCodes = request.Accounts
                .Where(a => a.AccountType == "Main")
                .Select(a => a.CardCode)
                .ToHashSet();

            foreach (var sub in request.Accounts.Where(a => a.AccountType == "Sub"))
            {
                if (string.IsNullOrEmpty(sub.ParentCardCode) || !mainCardCodes.Contains(sub.ParentCardCode))
                {
                    return new LinkedAccountResponse
                    {
                        Success = false,
                        Message = $"Sub account '{sub.CardCode}' must reference a valid Main account as ParentCardCode. Available: {string.Join(", ", mainCardCodes)}"
                    };
                }
            }

            // Remove existing linked accounts
            if (user.LinkedAccounts.Any())
            {
                db.CustomerLinkedAccounts.RemoveRange(user.LinkedAccounts);
            }

            // Create new linked accounts
            foreach (var account in request.Accounts)
            {
                var allowedTransactions = account.AccountType == "Main"
                    ? "Invoice,Payment"
                    : "SalesOrder";

                db.CustomerLinkedAccounts.Add(new CustomerLinkedAccount
                {
                    CustomerPortalUserId = user.Id,
                    CardCode = account.CardCode,
                    CardName = account.CardName,
                    AccountType = account.AccountType,
                    Currency = account.Currency,
                    ParentCardCode = account.ParentCardCode,
                    IsDefault = account.IsDefault,
                    Description = account.Description ?? $"{account.AccountType} Account - {account.Currency ?? "Default"}",
                    AllowedTransactions = allowedTransactions,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }

            // Update user's account structure
            user.AccountStructure = "Multi";
            user.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Set up multi-account structure for {CardCode}: {MainCount} main, {SubCount} sub accounts",
                request.PortalCardCode,
                request.Accounts.Count(a => a.AccountType == "Main"),
                request.Accounts.Count(a => a.AccountType == "Sub"));

            var linkedAccounts = await GetLinkedAccountsAsync(request.PortalCardCode);

            return new LinkedAccountResponse
            {
                Success = true,
                Message = $"Multi-account structure configured with {request.Accounts.Count} accounts",
                AccountStructure = "Multi",
                LinkedAccounts = linkedAccounts
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up multi-account for {CardCode}", request.PortalCardCode);
            return new LinkedAccountResponse
            {
                Success = false,
                Message = $"Error setting up multi-account: {ex.Message}"
            };
        }
    }

    public async Task<LinkedAccountResponse> AddLinkedAccountAsync(string portalCardCode, LinkedAccountRequest request)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var user = await db.CustomerPortalUsers
                .Include(u => u.LinkedAccounts)
                .FirstOrDefaultAsync(u => u.CardCode == portalCardCode);

            if (user == null)
            {
                return new LinkedAccountResponse
                {
                    Success = false,
                    Message = $"Portal user with CardCode '{portalCardCode}' not found"
                };
            }

            // Check for duplicate
            if (user.LinkedAccounts.Any(la => la.CardCode == request.CardCode))
            {
                return new LinkedAccountResponse
                {
                    Success = false,
                    Message = $"Account '{request.CardCode}' is already linked to this user"
                };
            }

            var allowedTransactions = request.AccountType == "Main"
                ? "Invoice,Payment"
                : "SalesOrder";

            db.CustomerLinkedAccounts.Add(new CustomerLinkedAccount
            {
                CustomerPortalUserId = user.Id,
                CardCode = request.CardCode,
                CardName = request.CardName,
                AccountType = request.AccountType,
                Currency = request.Currency,
                ParentCardCode = request.ParentCardCode,
                IsDefault = request.IsDefault,
                Description = request.Description ?? $"{request.AccountType} Account - {request.Currency ?? "Default"}",
                AllowedTransactions = allowedTransactions,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            // Ensure account structure is set to Multi
            if (user.AccountStructure != "Multi")
            {
                user.AccountStructure = "Multi";
                user.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();

            _logger.LogInformation("Added linked {AccountType} account {LinkedCardCode} to {PortalCardCode}",
                request.AccountType, request.CardCode, portalCardCode);

            var linkedAccounts = await GetLinkedAccountsAsync(portalCardCode);

            return new LinkedAccountResponse
            {
                Success = true,
                Message = $"Account '{request.CardCode}' linked successfully",
                AccountStructure = user.AccountStructure,
                LinkedAccounts = linkedAccounts
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding linked account {LinkedCardCode} to {PortalCardCode}",
                request.CardCode, portalCardCode);
            return new LinkedAccountResponse
            {
                Success = false,
                Message = $"Error adding linked account: {ex.Message}"
            };
        }
    }

    public async Task<LinkedAccountResponse> RemoveLinkedAccountAsync(string portalCardCode, string linkedCardCode)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var user = await db.CustomerPortalUsers
                .Include(u => u.LinkedAccounts)
                .FirstOrDefaultAsync(u => u.CardCode == portalCardCode);

            if (user == null)
            {
                return new LinkedAccountResponse
                {
                    Success = false,
                    Message = $"Portal user with CardCode '{portalCardCode}' not found"
                };
            }

            var linkedAccount = user.LinkedAccounts.FirstOrDefault(la => la.CardCode == linkedCardCode);
            if (linkedAccount == null)
            {
                return new LinkedAccountResponse
                {
                    Success = false,
                    Message = $"Linked account '{linkedCardCode}' not found"
                };
            }

            // If removing a Main account, also remove its sub accounts
            if (linkedAccount.AccountType == "Main")
            {
                var subAccounts = user.LinkedAccounts
                    .Where(la => la.ParentCardCode == linkedCardCode)
                    .ToList();

                if (subAccounts.Any())
                {
                    db.CustomerLinkedAccounts.RemoveRange(subAccounts);
                    _logger.LogInformation("Also removed {Count} sub accounts under main {MainCardCode}",
                        subAccounts.Count, linkedCardCode);
                }
            }

            db.CustomerLinkedAccounts.Remove(linkedAccount);

            // Check if any linked accounts remain
            var remainingCount = user.LinkedAccounts.Count(la => la.CardCode != linkedCardCode && la.IsActive);
            if (remainingCount == 0)
            {
                user.AccountStructure = "Single";
            }

            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var linkedAccounts = await GetLinkedAccountsAsync(portalCardCode);

            return new LinkedAccountResponse
            {
                Success = true,
                Message = $"Account '{linkedCardCode}' removed",
                AccountStructure = user.AccountStructure,
                LinkedAccounts = linkedAccounts
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing linked account {LinkedCardCode} from {PortalCardCode}",
                linkedCardCode, portalCardCode);
            return new LinkedAccountResponse
            {
                Success = false,
                Message = $"Error removing linked account: {ex.Message}"
            };
        }
    }

    public async Task<LinkedAccountResponse> ConvertToSingleAccountAsync(string portalCardCode)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var user = await db.CustomerPortalUsers
                .Include(u => u.LinkedAccounts)
                .FirstOrDefaultAsync(u => u.CardCode == portalCardCode);

            if (user == null)
            {
                return new LinkedAccountResponse
                {
                    Success = false,
                    Message = $"Portal user with CardCode '{portalCardCode}' not found"
                };
            }

            if (user.LinkedAccounts.Any())
            {
                db.CustomerLinkedAccounts.RemoveRange(user.LinkedAccounts);
            }

            user.AccountStructure = "Single";
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation("Converted {CardCode} back to single-account structure", portalCardCode);

            return new LinkedAccountResponse
            {
                Success = true,
                Message = "Converted to single-account structure",
                AccountStructure = "Single",
                LinkedAccounts = new List<LinkedAccountInfo>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting {CardCode} to single account", portalCardCode);
            return new LinkedAccountResponse
            {
                Success = false,
                Message = $"Error converting to single account: {ex.Message}"
            };
        }
    }

    public async Task<bool> CanPerformTransactionAsync(string portalCardCode, string targetCardCode, string transactionType)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var user = await db.CustomerPortalUsers
            .Include(u => u.LinkedAccounts)
            .FirstOrDefaultAsync(u => u.CardCode == portalCardCode);

        if (user == null) return false;

        // Single account: all transactions allowed on the main CardCode
        if (user.AccountStructure == "Single")
            return portalCardCode == targetCardCode;

        // Multi account: check the linked account's allowed transactions
        var linkedAccount = user.LinkedAccounts
            .FirstOrDefault(la => la.CardCode == targetCardCode && la.IsActive);

        if (linkedAccount == null) return false;

        var allowed = linkedAccount.AllowedTransactions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return allowed.Contains(transactionType, StringComparer.OrdinalIgnoreCase);
    }

    private static LinkedAccountInfo MapToLinkedAccountInfo(CustomerLinkedAccount entity)
    {
        return new LinkedAccountInfo
        {
            Id = entity.Id,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            AccountType = entity.AccountType,
            Currency = entity.Currency,
            ParentCardCode = entity.ParentCardCode,
            Description = entity.Description,
            AllowedTransactions = entity.AllowedTransactions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            IsDefault = entity.IsDefault,
            IsActive = entity.IsActive
        };
    }
}
