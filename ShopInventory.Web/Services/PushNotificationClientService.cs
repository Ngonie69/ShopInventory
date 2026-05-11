using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IPushNotificationClientService
{
    Task<PushSendResult> SendImportantPushAsync(ImportantPushRequest request, CancellationToken cancellationToken = default);
    Task<PushSendResult> SendTestPushAsync(CancellationToken cancellationToken = default);
}

public sealed class PushNotificationClientService(
    HttpClient httpClient,
    ILogger<PushNotificationClientService> logger) : IPushNotificationClientService
{
    public async Task<PushSendResult> SendImportantPushAsync(ImportantPushRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                TargetUsername = request.TargetUsername,
                TargetRole = request.TargetRole,
                request.Title,
                request.Body,
                Data = new Dictionary<string, string>
                {
                    ["importance"] = "important",
                    ["source"] = "web",
                    ["category"] = "Important"
                }
            };

            using var response = await httpClient.PostAsJsonAsync("api/PushNotification/send", payload, cancellationToken);
            return await ReadPushResultAsync(response, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending important push notification");
            return PushSendResult.Failed("The push notification could not be sent.");
        }
    }

    public async Task<PushSendResult> SendTestPushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PostAsync("api/PushNotification/test", null, cancellationToken);
            return await ReadPushResultAsync(response, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending test push notification");
            return PushSendResult.Failed("The test push notification could not be sent.");
        }
    }

    private static async Task<PushSendResult> ReadPushResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            return PushSendResult.Failed(string.IsNullOrWhiteSpace(detail)
                ? $"Push send failed with status {(int)response.StatusCode}."
                : detail);
        }

        var result = await response.Content.ReadFromJsonAsync<PushSendApiResponse>(cancellationToken);
        return PushSendResult.Succeeded(result?.Sent ?? 0, result?.Title ?? result?.Message ?? "Push notification");
    }

    private sealed class PushSendApiResponse
    {
        public int Sent { get; set; }
        public string? Title { get; set; }
        public string? Message { get; set; }
    }
}

public sealed record ImportantPushRequest(
    string? TargetUsername,
    string? TargetRole,
    string Title,
    string Body);

public sealed record PushSendResult(bool IsSuccess, int Sent, string Message)
{
    public static PushSendResult Succeeded(int sent, string message) => new(true, sent, message);
    public static PushSendResult Failed(string message) => new(false, 0, message);
}