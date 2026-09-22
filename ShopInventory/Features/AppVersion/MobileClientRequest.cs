namespace ShopInventory.Features.AppVersion;

/// <summary>
/// What the Android apps put on every request, and what it takes to recognise one.
/// </summary>
/// <remarks>
/// <para>
/// There is no single header that says "I am one of the phones". The four apps grew their headers
/// at different times: the POD app has sent <c>X-Device-Model</c> since before any of the others
/// existed, the version headers came with the version policy, and <c>X-App-Id</c> came last. So
/// recognition is a judgement over three headers rather than a lookup, and it lives here because
/// two middlewares now have to make it the same way — a phone the version gate treats as a phone
/// and the maintenance gate does not would be a hole in whichever gate is stricter.
/// </para>
/// <para>
/// The rule is the one <c>MobileVersionEnforcementMiddleware</c> has enforced in production
/// since the version policy shipped: a caller naming Android is a phone, a caller naming any other
/// platform is not, and a caller naming no platform at all is a phone only if it carries the
/// version or device metadata that nothing but the apps sends. The Web calls the API with neither,
/// so it is never mistaken for one.
/// </para>
/// </remarks>
public sealed record MobileClientRequest
{
    public const string AppIdHeaderName = "X-App-Id";
    public const string PlatformHeaderName = "X-App-Platform";
    public const string VersionHeaderName = "X-App-Version";
    public const string DeviceModelHeaderName = "X-Device-Model";
    public const string AndroidPlatform = "android";

    /// <summary>Whether this request came from one of the Android apps.</summary>
    public required bool IsMobileApp { get; init; }

    /// <summary>The raw <c>X-App-Id</c>, as sent. Absent on older builds.</summary>
    public string? AppId { get; init; }

    /// <summary>
    /// The catalogue key <see cref="AppId"/> resolves to, or null when the app did not name itself
    /// or named something unknown. Null means "a phone, app unidentified" — which is deliberately
    /// not the same as "not a phone".
    /// </summary>
    public string? PolicyKey { get; init; }

    public string? Platform { get; init; }
    public string? Version { get; init; }
    public string? DeviceModel { get; init; }

    /// <summary>Whether the caller said <c>android</c> outright, rather than being inferred.</summary>
    public required bool NamesAndroid { get; init; }

    /// <summary>Whether the caller sent a version or a device model — headers only the apps send.</summary>
    public required bool HasMobileMetadata { get; init; }

    public static MobileClientRequest FromHeaders(IHeaderDictionary headers)
    {
        var platform = headers[PlatformHeaderName].FirstOrDefault();
        var appId = headers[AppIdHeaderName].FirstOrDefault();
        var version = headers[VersionHeaderName].FirstOrDefault();
        var deviceModel = headers[DeviceModelHeaderName].FirstOrDefault();

        var namesAndroid = string.Equals(platform?.Trim(), AndroidPlatform, StringComparison.OrdinalIgnoreCase);
        var namesSomePlatform = !string.IsNullOrWhiteSpace(platform);
        var hasMobileMetadata = !string.IsNullOrWhiteSpace(version) || !string.IsNullOrWhiteSpace(deviceModel);

        var isMobileApp = namesAndroid || (!namesSomePlatform && hasMobileMetadata);

        return new MobileClientRequest
        {
            IsMobileApp = isMobileApp,
            NamesAndroid = namesAndroid,
            HasMobileMetadata = hasMobileMetadata,
            AppId = appId,
            PolicyKey = MobileVersionPolicyAppCatalog.TryResolvePolicyKey(appId, out var policyKey) ? policyKey : null,
            Platform = platform,
            Version = version,
            DeviceModel = deviceModel
        };
    }
}
