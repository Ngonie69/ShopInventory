namespace ShopInventory.Features.AppVersion;

public interface IMobileVersionPolicyEvaluator
{
    MobileVersionPolicyEvaluation Evaluate(string? platform, string? currentVersion);
}