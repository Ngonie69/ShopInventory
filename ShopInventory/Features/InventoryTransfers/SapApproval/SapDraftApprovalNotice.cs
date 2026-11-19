using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.SapApproval;

/// <summary>What became of the B1 inbox message about a draft saved for SAP's approval.</summary>
/// <param name="Recipients">The SAP user codes the message went to; empty when it went to nobody.</param>
/// <param name="NotOriginators">Recipients who are on none of the matched templates' originator lists:
/// SAP posts the draft without approval if one of them adds it.</param>
/// <param name="Problem">Why it was not sent, when it was not.</param>
public sealed record SapDraftNoticeResult(
    IReadOnlyList<string> Recipients,
    IReadOnlyList<string> NotOriginators,
    string? Problem)
{
    public bool Sent => Recipients.Count > 0 && Problem is null;

    /// <summary>One sentence for the person who converted: who was told, or that nobody was.</summary>
    public string Sentence => Sent
        ? $"SAP's approver{(Recipients.Count == 1 ? "" : "s")} ({string.Join(", ", Recipients)}) {(Recipients.Count == 1 ? "has" : "have")} a message in the SAP inbox linking to the draft."
        : $"No SAP inbox message was sent ({Problem}), so tell SAP's approver yourself.";

    public static SapDraftNoticeResult NotSent(string problem) => new([], [], problem);
}

/// <summary>
/// Tells SAP's approvers, in their B1 inbox, that a transfer draft is waiting to be added. SAP opens its
/// approval only when a B1 user adds the draft; nothing the Service Layer does starts it (re-tested on
/// KEFALOS_TEST_3 on 2026-11-19: draft 80193 sat at <c>sasWithout</c> with no approval request). So
/// without this nobody in B1 hears of the draft.
/// </summary>
public interface ISapDraftApprovalNotice
{
    /// <summary>
    /// Sends the message to the approvers on every stage of <paramref name="route"/>'s templates. Never
    /// throws: the draft is saved either way, and the result says whether anyone was told.
    /// </summary>
    /// <param name="draft">The draft just saved.</param>
    /// <param name="route">The verdict that sent it to SAP's approval.</param>
    /// <param name="about">What the draft is, for the message: "Transfer request #11067 (WASHBAY to PROYG 2)".</param>
    /// <param name="orderNumber">The draft's <c>U_OrderNumber</c>, which tells drafts with the same number apart.</param>
    Task<SapDraftNoticeResult> SendAsync(
        SAPStockTransferDraft draft,
        SapTransferRoute route,
        string about,
        string? orderNumber,
        CancellationToken cancellationToken);
}

/// <remarks>
/// <para>The approvers are read from SAP when the draft is saved (each template's stages, then each
/// stage's approvers), not cached: a stage's list changes in B1 without the app hearing of it.</para>
///
/// <para>The link opens SAP object 112 (a draft) by <c>DocEntry</c>. Draft numbers repeat (two test drafts
/// both read 112390), so the text also names the order number.</para>
///
/// <para>An approver who is not one of the template's originators can open the draft, but if they press
/// Add, SAP posts it as an ordinary transfer: the template only holds its originators' documents. Those
/// approvers still get the message and are named in the log and the result.</para>
/// </remarks>
public sealed class SapDraftApprovalNotice(
    ISAPServiceLayerClient sap,
    ILogger<SapDraftApprovalNotice> logger) : ISapDraftApprovalNotice
{
    /// <summary>SAP object type of a draft (ODRF), which a message link opens by <c>DocEntry</c>.</summary>
    internal const string DraftObjectType = "112";

    /// <summary>B1 cuts a longer subject; kept well inside it.</summary>
    internal const int SubjectLength = 100;

    public async Task<SapDraftNoticeResult> SendAsync(
        SAPStockTransferDraft draft,
        SapTransferRoute route,
        string about,
        string? orderNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            var stageCodes = route.Templates
                .SelectMany(template => template.StageCodes ?? [])
                .Distinct()
                .ToList();
            if (stageCodes.Count == 0)
                return Unsent(draft, $"SAP's template {route.TemplateNames} names no approval stage");

            var approverIds = new List<int>();
            foreach (var code in stageCodes)
            {
                var stage = await sap.GetApprovalStageAsync(code, cancellationToken);
                approverIds.AddRange((stage?.ApprovalStageApprovers ?? [])
                    .Where(approver => approver.UserID is > 0)
                    .Select(approver => approver.UserID!.Value));
            }

            approverIds = approverIds.Distinct().ToList();
            if (approverIds.Count == 0)
                return Unsent(draft, $"the approval stage{(stageCodes.Count == 1 ? "" : "s")} of {route.TemplateNames} list{(stageCodes.Count == 1 ? "s" : "")} no approver");

            var users = await sap.GetSapUsersAsync(approverIds, cancellationToken);
            var recipients = users
                .Where(user => !string.IsNullOrWhiteSpace(user.UserCode))
                .OrderBy(user => user.UserCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
                return Unsent(draft, "SAP did not name the approvers' user codes");

            var originators = route.Templates
                .SelectMany(template => template.OriginatorIds ?? [])
                .ToHashSet();
            var notOriginators = recipients
                .Where(user => !originators.Contains(user.InternalKey))
                .Select(user => user.UserCode!.Trim())
                .ToList();
            if (notOriginators.Count > 0)
            {
                logger.LogWarning(
                    "SAP draft {DraftEntry}: approver(s) {Users} are not originators of {Templates}; if one of them adds the draft, " +
                    "SAP posts it without approval", draft.DocEntry, string.Join(", ", notOriginators), route.TemplateNames);
            }

            var codes = recipients.Select(user => user.UserCode!.Trim()).ToList();
            var message = Compose(draft, route, about, orderNumber, codes);
            var sent = await sap.SendInboxMessageAsync(message, cancellationToken);

            logger.LogInformation(
                "SAP draft {DraftEntry}: inbox message {MessageCode} sent to {Users} for {Templates}",
                draft.DocEntry, sent, string.Join(", ", codes), route.TemplateNames);
            return new SapDraftNoticeResult(codes, notOriginators, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "SAP draft {DraftEntry}: the B1 inbox message to SAP's approvers was not sent", draft.DocEntry);
            return SapDraftNoticeResult.NotSent($"it failed: {exception.Message}");
        }
    }

    /// <summary>The message itself: what the draft is, and that adding it in B1 is what starts SAP's approval.</summary>
    internal static SapInboxMessage Compose(
        SAPStockTransferDraft draft,
        SapTransferRoute route,
        string about,
        string? orderNumber,
        IReadOnlyList<string> recipients)
    {
        var number = string.IsNullOrWhiteSpace(orderNumber)
            ? $"draft {draft.DocNum}"
            : $"draft {draft.DocNum} (order number {orderNumber.Trim()})";
        var subject = $"Transfer draft {draft.DocNum} waiting for approval: {draft.FromWarehouse} to {draft.ToWarehouse}";
        if (subject.Length > SubjectLength)
            subject = subject[..SubjectLength];

        var text =
            $"{about} was saved to SAP as inventory transfer {number}, because SAP's approval {route.TemplateNames} covers it.{Environment.NewLine}{Environment.NewLine}" +
            $"Open the draft from the link below and press Add. SAP then asks for approval under {route.TemplateNames}. " +
            "Approve it, and the transfer can then be added from ShopInventory. Nothing has moved yet.";

        return new SapInboxMessage(
            subject,
            text,
            recipients,
            new SapInboxLink("Draft", draft.DocNum.ToString(System.Globalization.CultureInfo.InvariantCulture), DraftObjectType,
                draft.DocEntry.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private SapDraftNoticeResult Unsent(SAPStockTransferDraft draft, string problem)
    {
        logger.LogWarning("SAP draft {DraftEntry}: no B1 inbox message sent: {Problem}", draft.DocEntry, problem);
        return SapDraftNoticeResult.NotSent(problem);
    }
}
