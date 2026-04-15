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
    /// Send push notification to all registered devices
    /// </summary>
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
                        Credential = GoogleCredential.FromFile(_settings.ServiceAccountKeyPath),
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
        var tokens = await _context.PushDeviceRegistrations
            .AsNoTracking()
            .Where(d => d.User != null && d.User.Role == role && !d.IsRevoked)
            .Select(d => d.DeviceToken)
            .ToListAsync(ct);

        return await SendToTokensAsync(tokens, title, body, data, ct);
    }

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

    private async Task<int> SendToTokensAsync(List<string> tokens, string title, string body, Dictionary<string, string>? data, CancellationToken ct)
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

        var notification = new FirebaseAdmin.Messaging.Notification
        {
            Title = title,
            Body = body
        };

        var sent = 0;
        var revokedTokens = new List<string>();

        // FCM supports up to 500 tokens per multicast
        foreach (var batch in tokens.Chunk(500))
        {
            var message = new MulticastMessage
            {
                Tokens = batch.ToList(),
                Notification = notification,
                Data = data,
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        ClickAction = "OPEN_NOTIFICATION",
                        Sound = "default"
                    }
                },
                Apns = new ApnsConfig
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
                    if (!response.Responses[i].IsSuccess)
                    {
                        var error = response.Responses[i].Exception?.MessagingErrorCode;
                        if (error == MessagingErrorCode.Unregistered || error == MessagingErrorCode.InvalidArgument)
                        {
                            revokedTokens.Add(batch[i]);
                        }
                        _logger.LogWarning("FCM send failed for token {Token}: {Error}", batch[i][..12] + "...", error);
                    }
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

        _logger.LogInformation("Push notification sent: {Sent}/{Total} devices. Title: {Title}", sent, tokens.Count, title);
        return sent;
    }

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
