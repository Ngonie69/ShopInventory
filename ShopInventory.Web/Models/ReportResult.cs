namespace ShopInventory.Web.Models;

/// <summary>
/// A report read from the API, or the sentence saying why it could not be.
/// </summary>
/// <remarks>
/// The van report reads used to return null on any failure, so a page could only guess at the cause
/// and every page guessed "check that the API is reachable" — including when the API had answered
/// with its own reason, such as a period longer than it allows. <see cref="Error"/> is that reason,
/// in words for the screen.
/// </remarks>
public readonly record struct ReportResult<T>(T? Value, string? Error) where T : class
{
    public static ReportResult<T> Success(T value) => new(value, null);

    public static ReportResult<T> Failure(string error) => new(null, error);
}
