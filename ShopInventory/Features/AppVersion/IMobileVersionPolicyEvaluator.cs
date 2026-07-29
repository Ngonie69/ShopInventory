namespace ShopInventory.Features.AppVersion;

public interface IMobileVersionPolicyEvaluator
{
    MobileVersionPolicyEvaluation Evaluate(string? appId, string? platform, string? currentVersion);
}