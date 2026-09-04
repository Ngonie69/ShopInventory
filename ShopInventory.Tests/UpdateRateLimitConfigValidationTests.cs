using ShopInventory.DTOs;
using ShopInventory.Features.RateLimit.Commands.UpdateRateLimitConfig;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What <c>PUT /api/RateLimit/config</c> refuses to save.
///
/// These limits now reach the live ASP.NET Core limiter, and its partition factory runs on the
/// request path for every request. A permit limit of zero or a zero-length window makes that
/// factory throw, so saving one would take the whole API down and keep it down - no restart clears
/// a bad row. Validation here is the difference between a 400 and an outage.
/// </summary>
public sealed class UpdateRateLimitConfigValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_000_000)]
    public async Task A_permit_limit_that_would_break_the_limiter_is_refused(int maxRequests)
    {
        var result = await HandleAsync(Config(maxRequests: maxRequests));

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, error => error.Code == "RateLimit.InvalidConfiguration");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(200_000)]
    public async Task A_window_that_would_break_the_limiter_is_refused(int windowSeconds)
    {
        var result = await HandleAsync(Config(windowSizeSeconds: windowSeconds));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_whitelist_entry_that_is_not_an_address_is_refused()
    {
        // A typo here reads as a working exemption. Nobody notices until the caller it was meant to
        // exempt is throttled, which is a support ticket rather than an error.
        var result = await HandleAsync(Config(whitelistedIPs: ["10.10.10.6", "not-an-ip"]));

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, error => error.Description.Contains("not-an-ip"));
    }

    [Fact]
    public async Task A_sane_configuration_is_accepted_and_written()
    {
        var service = new RecordingRateLimitService();

        var result = await new UpdateRateLimitConfigHandler(service)
            .Handle(new UpdateRateLimitConfigCommand(Config()), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(service.Written);
        Assert.Equal(100, service.Written!.MaxRequests);
    }

    [Fact]
    public async Task A_refused_configuration_is_never_written()
    {
        // The point of validating before the write rather than catching afterwards.
        var service = new RecordingRateLimitService();

        await new UpdateRateLimitConfigHandler(service)
            .Handle(new UpdateRateLimitConfigCommand(Config(maxRequests: 0)), CancellationToken.None);

        Assert.Null(service.Written);
    }

    private static async Task<ErrorOr.ErrorOr<string>> HandleAsync(RateLimitConfigDto config) =>
        await new UpdateRateLimitConfigHandler(new RecordingRateLimitService())
            .Handle(new UpdateRateLimitConfigCommand(config), CancellationToken.None);

    private static RateLimitConfigDto Config(
        int maxRequests = 100,
        int windowSizeSeconds = 60,
        int blockDurationMinutes = 15,
        List<string>? whitelistedIPs = null) => new()
        {
            MaxRequests = maxRequests,
            WindowSizeSeconds = windowSizeSeconds,
            BlockDurationMinutes = blockDurationMinutes,
            IsEnabled = true,
            WhitelistedIPs = whitelistedIPs ?? [],
            WhitelistedApiKeys = []
        };

    /// <summary>Records the write so a test can assert it did not happen.</summary>
    private sealed class RecordingRateLimitService : IRateLimitService
    {
        public RateLimitConfigDto? Written { get; private set; }

        public Task UpdateConfigurationAsync(RateLimitConfigDto config, CancellationToken cancellationToken = default)
        {
            Written = config;
            return Task.CompletedTask;
        }

        public RateLimitConfigDto GetConfiguration() => new();

        public Task<RateLimitDashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiRateLimitDto?> GetClientLimitAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> CheckRateLimitAsync(string clientId, string clientType, string? endpoint = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task IncrementRequestCountAsync(string clientId, string clientType, string? endpoint = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> UnblockClientAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task UpdateSettingsAsync(UpdateRateLimitSettingsRequest settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CleanupOldRecordsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<RateLimitListResponseDto> GetAllAsync(int page, int pageSize, bool? blockedOnly = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiRateLimitDto?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<RateLimitStatusDto> GetRateLimitStatusAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> IsRequestAllowedAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task BlockClientAsync(string clientId, int durationMinutes, string? reason = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> ResetClientAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<List<ApiRateLimitDto>> GetBlockedClientsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<RateLimitStatsDto> GetStatsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
