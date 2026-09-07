using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <inheritdoc />
public sealed class OpenWAWebhookRegistrar(
    IOpenWAClient client,
    IOptions<OpenWASettings> settings,
    ILogger<OpenWAWebhookRegistrar> logger) : IOpenWAWebhookRegistrar
{
    private readonly IOpenWAClient _client = client;
    private readonly OpenWASettings _settings = settings.Value;
    private readonly ILogger<OpenWAWebhookRegistrar> _logger = logger;

    public async Task<WhatsAppWebhookStatusDto> EnsureAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var status = BuildStatus(sessionId);

        var configurationProblem = DescribeConfigurationProblem();
        if (configurationProblem is not null)
        {
            status.Action = "failed";
            status.Message = configurationProblem;
            return status;
        }

        try
        {
            var existing = await _client.GetSessionWebhooksAsync(sessionId, cancellationToken);
            var matches = existing.Where(webhook => UrlMatches(webhook.Url)).ToList();

            var request = new WhatsAppWebhookRegistrationRequestDto
            {
                Url = _settings.WebhookPublicUrl.Trim(),
                Events = [.. NormalizedEvents()],
                // Written on every ensure rather than only on create. A webhook read back from
                // OpenWA cannot be checked against OpenWA:WebhookSecret, so a mismatch between the
                // two is invisible until every delivery starts failing signature verification.
                Secret = _settings.WebhookSecret.Trim()
            };

            if (matches.Count == 0)
            {
                // Active is deliberately left unset here: OpenWA's create DTO does not declare it,
                // its validation pipe forbids properties it does not declare, and a new webhook is
                // active by default. Sending it makes the create a 400.
                var created = await _client.CreateSessionWebhookAsync(sessionId, request, cancellationToken);
                _logger.LogInformation(
                    "Registered OpenWA webhook {WebhookId} for session {SessionId} at {Url}",
                    created.Id,
                    sessionId,
                    request.Url);

                return Describe(status, created, "created", null);
            }

            var target = matches.FirstOrDefault(webhook => webhook.Active) ?? matches[0];

            // The update DTO does declare Active, and setting it is what revives a webhook someone
            // disabled from the OpenWA dashboard.
            request.Active = true;
            var updated = await _client.UpdateSessionWebhookAsync(sessionId, target.Id, request, cancellationToken);

            var duplicateNote = matches.Count > 1
                ? $"OpenWA holds {matches.Count} webhooks for this URL. {target.Id} is the one this API maintains."
                : null;

            _logger.LogInformation(
                "Refreshed OpenWA webhook {WebhookId} for session {SessionId} at {Url}",
                updated.Id,
                sessionId,
                request.Url);

            return Describe(status, updated, "updated", duplicateNote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register the OpenWA webhook for session {SessionId}", sessionId);
            status.Action = "failed";
            status.Message = $"OpenWA rejected the webhook registration for this session. {ex.Message}";
            return status;
        }
    }

    public async Task<WhatsAppWebhookStatusDto> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var status = BuildStatus(sessionId);

        var configurationProblem = DescribeConfigurationProblem();
        if (configurationProblem is not null)
        {
            status.Action = "failed";
            status.Message = configurationProblem;
            return status;
        }

        try
        {
            var existing = await _client.GetSessionWebhooksAsync(sessionId, cancellationToken);
            var match = existing.FirstOrDefault(webhook => UrlMatches(webhook.Url) && webhook.Active)
                ?? existing.FirstOrDefault(webhook => UrlMatches(webhook.Url));

            if (match is null)
            {
                status.Message = existing.Count == 0
                    ? "OpenWA holds no webhook for this session, so inbound messages are discarded."
                    : $"OpenWA holds {existing.Count} webhook(s) for this session, none aimed at {status.ExpectedUrl}.";
                return status;
            }

            return Describe(status, match, "unchanged", match.Active ? null : "The webhook exists but is inactive.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the OpenWA webhook for session {SessionId}", sessionId);
            status.Action = "failed";
            status.Message = $"OpenWA did not answer when asked for this session's webhooks. {ex.Message}";
            return status;
        }
    }

    private WhatsAppWebhookStatusDto BuildStatus(string sessionId)
    {
        return new WhatsAppWebhookStatusDto
        {
            SessionId = sessionId,
            ExpectedUrl = _settings.WebhookPublicUrl?.Trim() ?? string.Empty,
            Events = [.. NormalizedEvents()]
        };
    }

    private static WhatsAppWebhookStatusDto Describe(
        WhatsAppWebhookStatusDto status,
        WhatsAppWebhookRegistrationDto webhook,
        string action,
        string? note)
    {
        status.Registered = webhook.Active;
        status.WebhookId = webhook.Id;
        status.Events = webhook.Events;
        status.Action = action;
        status.LastTriggeredAtUtc = webhook.LastTriggeredAt;
        status.Message = note;
        return status;
    }

    private string? DescribeConfigurationProblem()
    {
        if (!_settings.Enabled)
        {
            return "WhatsApp integration is disabled, so no webhook was registered.";
        }

        var url = _settings.WebhookPublicUrl?.Trim();

        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("${", StringComparison.Ordinal))
        {
            return "OpenWA:WebhookPublicUrl is not configured, so OpenWA has no address to deliver inbound messages to.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return $"OpenWA:WebhookPublicUrl is not an absolute URL: {url}";
        }

        // OpenWA validates the URL with class-validator's IsUrl, which rejects the bare hostname
        // "localhost". A same-host deployment has to name 127.0.0.1 or the LAN address instead.
        if (string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenWA rejects webhook URLs on localhost. Set OpenWA:WebhookPublicUrl to 127.0.0.1 or the server's own address.";
        }

        if (string.IsNullOrWhiteSpace(_settings.WebhookSecret) || _settings.WebhookSecret.StartsWith("${", StringComparison.Ordinal))
        {
            return "OpenWA:WebhookSecret is not configured, so inbound deliveries could not be authenticated even if they arrived.";
        }

        return null;
    }

    private IEnumerable<string> NormalizedEvents()
    {
        var events = (_settings.WebhookEvents ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // OpenWA's CreateWebhookDto enforces ArrayMinSize(1) whenever events are supplied at all,
        // and an empty subscription would register a webhook that receives nothing.
        return events.Length > 0 ? events : ["message.received"];
    }

    private bool UrlMatches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(_settings.WebhookPublicUrl))
        {
            return false;
        }

        return string.Equals(
            candidate.Trim().TrimEnd('/'),
            _settings.WebhookPublicUrl.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
