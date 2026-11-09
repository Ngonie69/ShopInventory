using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactRegister;

/// <summary>
/// Every WhatsApp number saved on a customer, for the page that keeps them all in one place.
/// </summary>
/// <param name="Search">
/// A customer's name or code, the contact's name, who recorded it, or any run of the number's digits —
/// "1567" finds every number ending in it, "0771 234 567" the number however it was typed.
/// </param>
/// <param name="State">
/// <c>auto</c>, <c>request</c>, <c>optedout</c>, <c>notonwhatsapp</c> or <c>removed</c>; anything else
/// is every number still on a customer.
/// </param>
/// <param name="Source">Web, VanHandset or Till: where the consent was recorded.</param>
/// <param name="ContactId">One row, as the delivery log links to it.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">Rows a page, at most 200.</param>
public sealed record GetCustomerWhatsAppContactRegisterQuery(
    string? Search,
    string? State,
    string? Source,
    int? ContactId = null,
    int Page = 1,
    int PageSize = 50) : IRequest<ErrorOr<CustomerWhatsAppContactRegisterDto>>;
