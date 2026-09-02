namespace ShopInventory.Services;

/// <summary>
/// The bytes of one file attached to a SAP document, ready to be handed to an HTTP response.
/// </summary>
/// <remarks>
/// <see cref="Content"/> may own the Service Layer response it is being read from, so it must be
/// disposed exactly once — <c>File(stream, …)</c> results do that for a controller; anything else
/// should wrap it in a <c>using</c>.
/// </remarks>
public sealed record SapAttachmentDownload(Stream Content, string ContentType, string FileName) : IDisposable
{
    public void Dispose() => Content.Dispose();
}
