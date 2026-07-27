namespace ShopInventory.Middleware;

/// <summary>
/// Marks a controller or action whose SAP work is bulk rather than interactive, so
/// <see cref="SapRequestPriorityMiddleware"/> leaves it in the background queue.
/// </summary>
/// <remarks>
/// HTTP requests are treated as interactive by default because almost all of them are a person
/// waiting on a small read. The exceptions are the ones that arrive over HTTP but behave like a
/// job: reports that scan months of documents, backfills, manual sync triggers, bulk validation.
/// Letting those hold the interactive reservation would defeat its purpose — an order approval
/// would queue behind a report exactly as it used to queue behind the catalog sync.
///
/// Getting this wrong in the "forgot to annotate a small endpoint" direction costs nothing. Getting
/// it wrong the other way costs approval latency, which is why the heavy endpoints are annotated
/// explicitly rather than inferred.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class SapBackgroundWorkAttribute : Attribute;
