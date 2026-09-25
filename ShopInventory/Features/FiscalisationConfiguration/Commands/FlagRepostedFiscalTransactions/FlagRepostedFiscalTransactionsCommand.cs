using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.FlagRepostedFiscalTransactions;

/// <summary>
/// Flags "Not Fiscalised" invoice rows in the fiscal transaction log whose invoice was reposted after
/// the SAP update, so the fiscalisation work queue stops counting them.
/// </summary>
public sealed record FlagRepostedFiscalTransactionsCommand
    : IRequest<ErrorOr<FlagRepostedFiscalTransactionsResult>>;
