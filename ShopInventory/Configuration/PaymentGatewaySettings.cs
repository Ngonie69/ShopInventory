namespace ShopInventory.Configuration;

/// <summary>
/// Configuration settings for payment gateways
/// </summary>
public class PaymentGatewaySettings
{
    /// <summary>
    /// PayNow integration settings
    /// </summary>
    public PayNowSettings PayNow { get; set; } = new();

    /// <summary>
    /// Innbucks integration settings
    /// </summary>
    public InnbucksSettings Innbucks { get; set; } = new();

    /// <summary>
    /// Ecocash integration settings
    /// </summary>
    public EcocashSettings Ecocash { get; set; } = new();
}

/// <summary>
/// PayNow Zimbabwe payment gateway settings
/// </summary>
public class PayNowSettings
{
    public bool IsEnabled { get; set; } = false;
    public bool IsSandbox { get; set; } = true;
    public string IntegrationId { get; set; } = string.Empty;
    public string IntegrationKey { get; set; } = string.Empty;
    public string ResultUrl { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = string.Empty;
}

/// <summary>
/// Innbucks mobile payment settings
/// </summary>
public class InnbucksSettings
{
    public bool IsEnabled { get; set; } = false;
    public bool IsSandbox { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string MerchantId { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
}

/// <summary>
/// Ecocash mobile money settings
/// </summary>
public class EcocashSettings
{
    public bool IsEnabled { get; set; } = false;
    public bool IsSandbox { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string MerchantCode { get; set; } = string.Empty;
    public string MerchantPin { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
}
