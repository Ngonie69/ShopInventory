namespace ShopInventory.DTOs;

/// <summary>
/// DTO for Business Partner information
/// </summary>
public class BusinessPartnerDto
{
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public string? CardType { get; set; }
    public string? GroupCode { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Currency { get; set; }
    public decimal? Balance { get; set; }
    public bool IsActive { get; set; }
    public int? PriceListNum { get; set; }
    public string? PriceListName { get; set; }
    public int? PayTermGrpCode { get; set; }
    public string? Channel { get; set; }
    public string? VatRegNo { get; set; }
    public string? TinNumber { get; set; }
}

/// <summary>
/// The credit-control view of a business partner: the limit, what is already owed against it, and
/// the consolidating parent whose limit the account draws on, if it has one.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="BusinessPartnerDto"/>. This is read on the sales order
/// path with a rep waiting, so it selects only the credit fields, and it must never be served from
/// the cached BP list — a balance that is one sync old is the wrong number to block an order on.
/// </remarks>
public class BusinessPartnerCreditProfileDto
{
    public string CardCode { get; set; } = string.Empty;
    public string? CardName { get; set; }

    /// <summary>The BP's currency, used only to label the amounts in the message.</summary>
    public string? Currency { get; set; }

    /// <summary>OCRD.CreditLine. Zero or less means no limit is configured for this account.</summary>
    public decimal CreditLimit { get; set; }

    /// <summary>OCRD.Balance — posted A/R only, before anything still open.</summary>
    public decimal Balance { get; set; }

    /// <summary>Sales orders raised but not yet invoiced, so not yet in <see cref="Balance"/>.</summary>
    public decimal OpenOrdersBalance { get; set; }

    /// <summary>
    /// OCRD.FatherCard — the consolidating business partner this account hangs off. When set, the
    /// group's limit governs rather than (only) this account's own.
    /// </summary>
    public string? FatherCard { get; set; }
}

/// <summary>
/// DTO for SAP Payment Terms (OCTG table)
/// </summary>
public class PaymentTermsDto
{
    public int GroupNumber { get; set; }
    public string PaymentTermsGroupName { get; set; } = string.Empty;
    public int NumberOfAdditionalDays { get; set; }
    public int NumberOfAdditionalMonths { get; set; }
}

/// <summary>
/// Response DTO for list of Business Partners
/// </summary>
public class BusinessPartnerListResponseDto
{
    public int TotalCount { get; set; }
    public List<BusinessPartnerDto>? BusinessPartners { get; set; }
}

/// <summary>
/// Response DTO for paginated Business Partners
/// </summary>
public class BusinessPartnerPagedResponseDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Count { get; set; }
    public bool HasMore { get; set; }
    public List<BusinessPartnerDto>? BusinessPartners { get; set; }
}
