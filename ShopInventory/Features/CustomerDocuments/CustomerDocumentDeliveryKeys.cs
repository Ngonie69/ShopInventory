using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>
/// Where the run-time switches for sending customer documents live in <c>SystemConfigs</c>, and how
/// they are read and written.
/// </summary>
/// <remarks>
/// <c>SystemConfigs</c> rather than configuration, for the reason the posting-date switch is there: an
/// administrator flips it from the web and every node honours it on its next pass, with no deploy and
/// no node left behind on an old value.
/// <para>
/// Until someone saves them no rows exist, and nothing has to be set up for a document to go: the
/// session is chosen from the gateway the first time one is needed (<see cref="CustomerDocumentSession"/>),
/// and automatic sending is on. Automatic sends still reach only the numbers a customer asked to
/// receive their invoices on, inside the window and under the daily cap. What an administrator saves
/// is always kept — a switch saved off stays off, and sending they stopped stays stopped.
/// </para>
/// </remarks>
internal static class CustomerDocumentDeliveryKeys
{
    public const string Category = "CustomerDocuments";
    public const string AutoSendEnabled = "CustomerDocuments.AutoSendEnabled";
    public const string WhatsAppSessionId = "CustomerDocuments.WhatsAppSessionId";
    public const string SendingStopped = "CustomerDocuments.SendingStopped";
    public const string MaxAutoPerDay = "CustomerDocuments.MaxAutoPerDay";
    public const string SettingsChangedBy = "CustomerDocuments.SettingsChangedBy";
    public const string AlertState = "CustomerDocuments.AlertState";

    /// <summary>A new number's first fortnight: low enough that WhatsApp sees a business, not a broadcast.</summary>
    public const int DefaultMaxAutoPerDay = 20;

    private static readonly string[] SettingKeys = [AutoSendEnabled, WhatsAppSessionId, SendingStopped, MaxAutoPerDay, SettingsChangedBy];

    public static async Task<CustomerDocumentRuntimeSettings> ReadAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.SystemConfigs
            .AsNoTracking()
            .Where(config => SettingKeys.Contains(config.Key))
            .Select(config => new { config.Key, config.Value, config.UpdatedAt })
            .ToListAsync(cancellationToken);

        string? Value(string key) => rows.FirstOrDefault(row => row.Key == key)?.Value;

        // On until someone saves it off: a number is marked for automatic invoices because the customer
        // asked for them, and they should not also wait on a switch nobody knew to flip.
        var autoSend = !bool.TryParse(Value(AutoSendEnabled), out var on) || on;
        var sessionId = string.IsNullOrWhiteSpace(Value(WhatsAppSessionId)) ? null : Value(WhatsAppSessionId)!.Trim();
        var stopped = sessionId is null && bool.TryParse(Value(SendingStopped), out var isStopped) && isStopped;
        var maxAuto = int.TryParse(Value(MaxAutoPerDay), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap) && cap >= 0
            ? cap
            : DefaultMaxAutoPerDay;
        var changed = rows.FirstOrDefault(row => row.Key == SettingsChangedBy);

        return new CustomerDocumentRuntimeSettings(
            autoSend,
            sessionId,
            stopped,
            maxAuto,
            changed?.Value,
            changed?.UpdatedAt);
    }

    /// <summary>Stages a value; the caller saves.</summary>
    public static async Task StageAsync(
        ApplicationDbContext db,
        string key,
        string valueType,
        string? value,
        string description,
        bool isEditable,
        CancellationToken cancellationToken)
    {
        var row = await db.SystemConfigs
            .AsTracking()
            .FirstOrDefaultAsync(config => config.Key == key, cancellationToken);

        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = key,
                ValueType = valueType,
                Category = Category,
                Description = description,
                IsEditable = isEditable
            };
            db.SystemConfigs.Add(row);
        }

        row.Value = value;
        row.UpdatedAt = DateTime.UtcNow;
    }
}
