using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>
/// Sends a till credit to the device that filed its original: only that one can credit it.
/// </summary>
/// <remarks>
/// <para>
/// Under the platform, the platform is asked first because it holds everything filed since the switch,
/// so crediting a new sale never depends on REVMax answering. REVMax is asked only when the platform says
/// plainly that it holds nothing, and only while <c>Revmax:Enabled</c> is on. If the platform cannot be
/// asked, the credit stops there: that is not an answer, and asking REVMax instead would read as one.
/// This is the order <see cref="RevmaxHistoryFiscalizationService"/> uses for a SAP credit note.
/// </para>
/// <para>
/// Under REVMax every credit is REVMax's, as it was before the switch.
/// </para>
/// <para>
/// A saved credit is finished where its original was found, which its plan records
/// (<see cref="DesktopCreditSource.OnPlatform"/>), so a retry never changes device.
/// </para>
/// </remarks>
public sealed class DesktopCreditFiscalRouter(PlatformDesktopCreditGateway platform, RevmaxDesktopCreditGateway revmax,
    IOptions<FiscalisationSettings> selection, IOptions<RevmaxSettings> revmaxSettings) : IDesktopCreditFiscalGateway
{
    public async Task<DesktopCreditSource> ReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct)
    {
        if (!selection.Value.UsesPlatform) return await revmax.ReadOriginalAsync(sale, ct);
        if (await platform.TryReadOriginalAsync(sale, ct) is { } source) return source;
        // Said plainly rather than as "REVMax must be enabled", which reads like a setting somebody could
        // go and switch on.
        if (!revmaxSettings.Value.Enabled)
            throw new InvalidOperationException(
                "The fiscalisation platform holds no receipt for this sale, and REVMax, which filed sales before "
                + "the switch, has been retired. Nothing here can credit it — raise the credit note in SAP.");
        return await revmax.ReadOriginalAsync(sale, ct);
    }

    public Task<FiscalizationResult?> FindAsync(DesktopCreditPlan plan, CancellationToken ct) =>
        For(plan).FindAsync(plan, ct);

    public Task<string?> PreflightAsync(DesktopCreditPlan plan, CancellationToken ct) =>
        For(plan).PreflightAsync(plan, ct);

    public Task<FiscalizationResult> SubmitAsync(DesktopCreditPlan plan, CancellationToken ct) =>
        For(plan).SubmitAsync(plan, ct);

    private IDesktopCreditFiscalGateway For(DesktopCreditPlan plan) => plan.Source.OnPlatform ? platform : revmax;
}
