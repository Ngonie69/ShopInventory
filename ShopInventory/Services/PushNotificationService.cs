using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

public interface IPushNotificationService
{
    /// <summary>
    /// Register or refresh a device token for a user
    /// </summary>
    Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Unregister a device token
    /// </summary>
    Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default);

    /// <summary>
    /// Get all registered devices for a user
    /// </summary>
    Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Send push notification to a specific user (all their devices)
    /// </summary>
    Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default);

    /// <summary>
    /// Send push notification to a user by username
    /// </summary>
    Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default);

    /// <summary>
    /// Send push notification to all users in a role
    /// </summary>
    Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default);

    /// <summary>
    /// Wake the app on every device in a role and hand it a payload, with nothing shown to whoever
    /// is holding the phone.
    /// </summary>
    /// <remarks>
    /// For a signal the app acts on rather than something a person is meant to read — a catalogue
    /// that needs refreshing, say. On Android a message carrying a notification block is handed to
    /// the system tray while the app is backgrounded and the app itself is never woken; a data-only
    /// message is delivered to the background handler instead, so this is both quieter and more
    /// likely to be acted on than the alert it replaces.
    /// </remarks>
    Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default);

    /// <summary>
    /// Send push notification to all registered devices
    /// </summary>
    /// <summary>
    /// Sends to a set of device tokens the caller has resolved itself.
    /// </summary>
    /// <remarks>
    /// The FCM transport on its own — batching, Android priority, and the invalidation of tokens
    /// Firebase reports as dead — without any assumption about whose devices these are. It exists so
    /// that a subject which is not a <c>User</c> can reuse the messaging setup without its
    /// registrations being added to <c>PushDeviceRegistrations</c>: everything else here resolves
    /// tokens from that table, and <see cref="SendToAllAsync"/> in particular takes every row in it,
    /// so a non-staff registration living there would receive staff broadcasts.
    /// </remarks>
    Task<int> SendToDeviceTokensAsync(
        IReadOnlyCollection<string> deviceTokens,
        string title,
        string body,
        Dictionary<string, string>? data = null,
        CancellationToken ct = default);

    Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default);

    /// <summary>
    /// Clean up revoked/stale device tokens
    /// </summary>
    Task CleanupStaleTokensAsync(CancellationToken ct = default);
}

public class PushNotificationService : IPushNotificationService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly FirebaseSettings _settings;
    private readonly FirebaseMessaging? _messaging;

    public PushNotificationService(
        ApplicationDbContext context,
        ILogger<PushNotificationService> logger,
        IOptions<FirebaseSettings> settings)
    {
        _context = context;
        _logger = logger;
        _settings = settings.Value;

        if (_settings.Enabled)
        {
            _messaging = InitializeFirebase();
        }
    }

    private static readonly object _firebaseLock = new();
    private static bool _firebaseInitialized;

    private FirebaseMessaging? InitializeFirebase()
    {
        try
        {
            if (FirebaseApp.DefaultInstance != null)
                return FirebaseMessaging.DefaultInstance;

            lock (_firebaseLock)
            {
                // Double-check after acquiring the lock
                if (FirebaseApp.DefaultInstance != null)
                    return FirebaseMessaging.DefaultInstance;

                if (_firebaseInitialized)
                    return FirebaseMessaging.DefaultInstance;

                AppOptions options;
                if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath) && File.Exists(_settings.ServiceAccountKeyPath))
                {
                    options = new AppOptions
                    {
#pragma warning disable CS0618 // GoogleCredential.FromFile deprecated — migrate to CredentialFactory when Firebase SDK is updated
                        Credential = GoogleCredential.FromFile(_settings.ServiceAccountKeyPath),
#pragma warning restore CS0618
                        ProjectId = _settings.ProjectId
                    };
                }
                else
                {
                    // Falls back to GOOGLE_APPLICATION_CREDENTIALS env var
                    options = new AppOptions
                    {
                        Credential = GoogleCredential.GetApplicationDefault(),
                        ProjectId = _settings.ProjectId
                    };
                }

                FirebaseApp.Create(options);
                _firebaseInitialized = true;
                _logger.LogInformation("Firebase Admin SDK initialized for push notifications");
                return FirebaseMessaging.DefaultInstance;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Firebase Admin SDK. Push notifications will be disabled");
            return null;
        }
    }

    public async Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default)
    {
        // Check if this token already exists
        var existing = await _context.PushDeviceRegistrations
            .FirstOrDefaultAsync(d => d.DeviceToken == request.DeviceToken, ct);

        if (existing != null)
        {
            // Re-assign to current user (token may have moved to a different user login)
            existing.UserId = userId;
            existing.Platform = request.Platform;
            existing.DeviceName = request.DeviceName;
            existing.AppVersion = request.AppVersion;
            existing.RegisteredAt = DateTime.UtcNow;
            existing.IsRevoked = false;
        }
        else
        {
            existing = new PushDeviceRegistration
            {
                UserId = userId,
                DeviceToken = request.DeviceToken,
                Platform = request.Platform,
                DeviceName = request.DeviceName,
                AppVersion = request.AppVersion,
                RegisteredAt = DateTime.UtcNow
            };
            _context.PushDeviceRegistrations.Add(existing);
        }

        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Device registered for user {UserId}: {Platform} {DeviceName}", userId, request.Platform, request.DeviceName);

        return MapToDto(existing);
    }

    public async Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default)
    {
        var device = await _context.PushDeviceRegistrations
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceToken == deviceToken, ct);

        if (device != null)
        {
            _context.PushDeviceRegistrations.Remove(device);
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Device unregistered for user {UserId}", userId);
        }
    }

    public async Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.PushDeviceRegistrations
            .AsNoTracking()
            .Where(d => d.UserId == userId && !d.IsRevoked)
            .OrderByDescending(d => d.RegisteredAt)
            .Select(d => MapToDto(d))
            .ToListAsync(ct);
    }

    public async Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
    {
        var tokens = await _context.PushDeviceRegistrations
            .AsNoTracking()
            .Where(d => d.UserId == userId && !d.IsRevoked)
            .Select(d => d.DeviceToken)
            .ToListAsync(ct);

        return await SendToTokensAsync(tokens, title, body, data, ct);
    }

    public async Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
    {
        var tokens = await _context.PushDeviceRegistrations
            .AsNoTracking()
            .Where(d => d.User != null && d.User.Username == username && !d.IsRevoked)
            .Select(d => d.DeviceToken)
            .ToListAsync(ct);

        return await SendToTokensAsync(tokens, title, body, data, ct);
    }

    public async Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
    {
        var tokens = await RoleTokensAsync(role, ct);

        return await SendToTokensAsync(tokens, title, body, data, ct);
    }

    public async Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default)
    {
        var tokens = await RoleTokensAsync(role, ct);

        return await SendToTokensAsync(tokens, notification: null, data, $"silent data push to {role}", ct);
    }

    private Task<List<string>> RoleTokensAsync(string role, CancellationToken ct) =>
        _context.PushDeviceRegistrations
            .AsNoTracking()
            .Where(d => d.User != null && d.User.Role == role && !d.IsRevoked)
            .Select(d => d.DeviceToken)
            .ToListAsync(ct);

    public async Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
    {
        var tokens = await _context.PushDeviceRegistrations
            .AsNoTracking()
            .Where(d => !d.IsRevoked)
            .Select(d => d.DeviceToken)
            .ToListAsync(ct);

        return await SendToTokensAsync(tokens, title, body, data, ct);
    }

    public async Task CleanupStaleTokensAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var staleTokens = await _context.PushDeviceRegistrations
            .Where(d => d.IsRevoked || d.RegisteredAt < cutoff && d.LastActiveAt == null || d.LastActiveAt < cutoff)
            .ToListAsync(ct);

        if (staleTokens.Count > 0)
        {
            _context.PushDeviceRegistrations.RemoveRange(staleTokens);
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Cleaned up {Count} stale push device tokens", staleTokens.Count);
        }
    }

    public Task<int> SendToDeviceTokensAsync(
        IReadOnlyCollection<string> deviceTokens,
        string title,
        string body,
        Dictionary<string, string>? data = null,
        CancellationToken ct = default)
        => SendToTokensAsync(deviceTokens.ToList(), title, body, data, ct);

    private Task<int> SendToTokensAsync(List<string> tokens, string title, string body, Dictionary<string, string>? data, CancellationToken ct) =>
        SendToTokensAsync(
            tokens,
            new FirebaseAdmin.Messaging.Notification { Title = title, Body = body },
            data,
            title,
            ct);

    private async Task<int> SendToTokensAsync(
        List<string> tokens,
        FirebaseAdmin.Messaging.Notification? notification,
        Dictionary<string, string>? data,
        string logLabel,
        CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            _logger.LogDebug("No device tokens to send push notification to");
            return 0;
        }

        if (_messaging == null)
        {
            _logger.LogWarning("Push notifications disabled — Firebase not initialized. Would have sent to {Count} devices", tokens.Count);
            return 0;
        }

        var sent = 0;
        var revokedTokens = new List<string>();

        // FCM supports up to 500 tokens per multicast
        foreach (var batch in tokens.Chunk(500))
        {
            var message = new MulticastMessage
            {
                // The deprecation points at Fids, which is a different identifier: a Firebase
                // installation id, not the FCM registration token a handset sends us at sign-in and
                // which is what PushDeviceRegistration.DeviceToken holds. Sending our tokens as Fids
                // would address nobody and fail silently, so this stays until we have a reason —
                // and a migration — to store installation ids instead.
#pragma warning disable CS0618
                Tokens = batch.ToList(),
#pragma warning restore CS0618
                Notification = notification,
                Data = data,
                // A silent message carries no tray entry and makes no sound: the platform config
                // has to leave those out too, and iOS additionally needs content-available before
                // it will wake a backgrounded app for a payload nobody is shown.
                Android = notification is null
                    ? new AndroidConfig { Priority = Priority.High }
                    : new AndroidConfig
                    {
                        Priority = Priority.High,
                        Notification = new AndroidNotification
                        {
                            ClickAction = "OPEN_NOTIFICATION",
                            Sound = "default"
                        }
                    },
                Apns = notification is null
                    ? new ApnsConfig { Aps = new Aps { ContentAvailable = true } }
                    : new ApnsConfig
                    {
                        Aps = new Aps
                        {
                            Sound = "default",
                            Badge = 1
                        }
                    }
            };

            try
            {
                var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message, ct);
                sent += response.SuccessCount;

                // Track failed tokens
                for (int i = 0; i < response.Responses.Count; i++)
                {
                    if (response.Responses[i].IsSuccess)
                    {
                        continue;
                    }

                    var failure = response.Responses[i].Exception;
                    var messagingError = failure?.MessagingErrorCode;

                    if (IsPermanentTokenFailure(messagingError))
                    {
                        revokedTokens.Add(batch[i]);
                    }

                    // MessagingErrorCode alone is null for anything FCM did not classify as a
                    // messaging fault — a transport failure, a quota rejection — and production
                    // duly logged "FCM send failed for token cIqH8sOKRVCP...: null", which says
                    // nothing at all and leaves the token unpruned with no way to find out why.
                    // The general error code and the message are what make it diagnosable.
                    _logger.LogWarning(
                        "FCM send failed for token {Token}: {MessagingError} / {ErrorCode} — {Reason}",
                        batch[i][..12] + "...",
                        messagingError?.ToString() ?? "unclassified",
                        failure?.ErrorCode.ToString() ?? "unknown",
                        failure?.Message ?? "no detail was returned");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FCM multicast send failed for batch of {Count} tokens", batch.Length);
            }
        }

        // Mark revoked tokens
        if (revokedTokens.Count > 0)
        {
            await _context.PushDeviceRegistrations
                .Where(d => revokedTokens.Contains(d.DeviceToken))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsRevoked, true), ct);

            _logger.LogInformation("Revoked {Count} invalid device tokens", revokedTokens.Count);
        }

        // Update LastActiveAt for successfully-sent tokens
        if (sent > 0)
        {
            var successTokens = tokens.Except(revokedTokens).ToList();
            await _context.PushDeviceRegistrations
                .Where(d => successTokens.Contains(d.DeviceToken))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastActiveAt, DateTime.UtcNow), ct);
        }

        _logger.LogInformation("Push notification sent: {Sent}/{Total} devices. Title: {Title}", sent, tokens.Count, logLabel);
        return sent;
    }

    /// <summary>
    /// Whether this failure means the token will never work again, so the registration should go.
    /// </summary>
    /// <remarks>
    /// Only codes that describe the registration itself. <c>Unregistered</c> is the app uninstalled
    /// or the token rotated; <c>InvalidArgument</c> is a malformed token; <c>SenderIdMismatch</c> is
    /// a token minted for a different Firebase project, which this one can never send to; and
    /// <c>ThirdPartyAuthError</c> is an APNs credential the token is permanently bound to.
    /// <para>
    /// Everything else — quota, unavailable, internal, and the unclassified failures that report no
    /// messaging code at all — is transient or unknown, and revoking on those would silently stop a
    /// working handset receiving anything.
    /// </para>
    /// </remarks>
    internal static bool IsPermanentTokenFailure(MessagingErrorCode? error) => error
        is MessagingErrorCode.Unregistered
        or MessagingErrorCode.InvalidArgument
        or MessagingErrorCode.SenderIdMismatch
        or MessagingErrorCode.ThirdPartyAuthError;

    private static DeviceRegistrationDto MapToDto(PushDeviceRegistration d)
    {
        return new DeviceRegistrationDto
        {
            Id = d.Id,
            DeviceToken = d.DeviceToken,
            Platform = d.Platform,
            DeviceName = d.DeviceName,
            AppVersion = d.AppVersion,
            RegisteredAt = d.RegisteredAt,
            LastActiveAt = d.LastActiveAt
        };
    }
}
