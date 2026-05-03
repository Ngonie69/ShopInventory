namespace ShopInventory.Common;

public static class BatchStatusValues
{
    public const string Released = "Released";
    public const string Locked = "Locked";
    public const string NotAccessible = "NotAccessible";

    public static readonly string[] SupportedStatuses =
    [
        Released,
        Locked,
        NotAccessible
    ];

    public static bool IsSupported(string? status)
        => TryNormalize(status, out _);

    public static bool TryNormalize(string? status, out string normalizedStatus)
    {
        normalizedStatus = string.Empty;

        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var sanitized = status.Trim().Replace("_", string.Empty).Replace(" ", string.Empty);

        normalizedStatus = sanitized.ToLowerInvariant() switch
        {
            "released" or "bdsstatusreleased" or "0" => Released,
            "locked" or "bdsstatuslocked" or "1" => Locked,
            "notaccessible" or "bdsstatusnotaccessible" or "2" => NotAccessible,
            _ => string.Empty
        };

        return normalizedStatus.Length > 0;
    }

    public static string ToSapValue(string status)
    {
        if (!TryNormalize(status, out var normalizedStatus))
        {
            throw new ArgumentException($"Unsupported batch status '{status}'.", nameof(status));
        }

        return normalizedStatus switch
        {
            Released => "bdsStatus_Released",
            Locked => "bdsStatus_Locked",
            NotAccessible => "bdsStatus_NotAccessible",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
    }

    public static string ToLabel(string? status)
    {
        if (!TryNormalize(status, out var normalizedStatus))
        {
            return string.IsNullOrWhiteSpace(status) ? "Unknown" : status.Trim();
        }

        return normalizedStatus switch
        {
            NotAccessible => "Not Accessible",
            _ => normalizedStatus
        };
    }
}