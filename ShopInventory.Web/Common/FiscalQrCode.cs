using System.Text;
using QRCoder;

namespace ShopInventory.Web.Common;

/// <summary>
/// Renders the string a fiscal device returned into a QR image a page can show.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for every page that shows one. Three pages now do — invoices, credit notes and
/// desktop sales — and the interesting part is not the six lines of QRCoder but the two decisions
/// underneath them, which are the things that would drift if each page kept its own copy: the
/// correction level, and what happens when the code cannot be rendered.
/// </para>
/// <para>
/// <b>Level Q.</b> A ZIMRA verification code is scanned off a printed receipt, often a thermal one
/// that has been in a pocket. Q recovers about a quarter of the symbol, which is what makes a
/// creased or smudged receipt still scan; the cost is a denser image, which matters not at all on a
/// screen and very little on paper at this size.
/// </para>
/// <para>
/// <b>A failure renders nothing rather than a broken image.</b> The caller shows the panel only when
/// this returns a source, so a code QRCoder cannot encode leaves the drawer with no QR section at
/// all — which is honest. A placeholder would be worse than nothing here: the one thing a fiscal QR
/// is for is being scanned, and something that looks like a QR and does not scan sends the customer
/// away believing they checked.
/// </para>
/// </remarks>
public static class FiscalQrCode
{
    /// <summary>
    /// A <c>data:</c> URI for the code, or null when there is nothing to show.
    /// </summary>
    public static string? ToImageSource(string? fiscalQrCode)
    {
        if (string.IsNullOrWhiteSpace(fiscalQrCode))
        {
            return null;
        }

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(fiscalQrCode.Trim(), QRCodeGenerator.ECCLevel.Q);
            var svg = new SvgQRCode(data).GetGraphic(8);
            return $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
        }
        catch
        {
            // Swallowed on purpose, and this is the one place it is swallowed. A drawer must open
            // even when the code is malformed: everything else in it — the amounts, the lines, the
            // customer — is what the operator came for, and none of it depends on the QR.
            return null;
        }
    }
}
