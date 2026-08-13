namespace ShopInventory.Services;

/// <summary>
/// The attachment store could not be written to. The upload itself was valid, so the caller should
/// send it again rather than treat it as a rejection.
/// </summary>
/// <remarks>
/// In production the attachment root is a network location, so the store disappears wholesale
/// rather than failing one file at a time. On 2026-08-13 that surfaced as an unhandled IOException
/// ("The network path was not found") and every POD upload for eighty minutes came back as a bare
/// 500 — indistinguishable, from the handset's side, from a permanent refusal.
/// </remarks>
public sealed class AttachmentStorageUnavailableException(string attachmentPath, Exception innerException)
    : Exception($"Attachment storage is unavailable: '{attachmentPath}'.", innerException)
{
    /// <summary>
    /// Server-side path that could not be reached. For logs only — it is deliberately kept out of
    /// the client-facing response.
    /// </summary>
    public string AttachmentPath { get; } = attachmentPath;
}
