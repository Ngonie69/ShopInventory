using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ShopInventory.Common.Mobile;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility;

public static partial class VanSalesCompatibilityMapper
{
    private const int LegacyVatRate = 15;
    private static readonly Regex TrailingDigitsRegex = new("(\\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// Builds the handset's login payload.
    /// </summary>
    /// <remarks>
    /// <c>assignedBusinessPartnerName</c> is passed in rather than looked up here because reading it
    /// touches SAP, and it is optional: empty means the handset shows the route's code instead. See
    /// <see cref="VanSalesRouteName"/> for why the handset can no longer work it out for itself.
    /// </remarks>
    public static VanSalesLoginResponse MapLoginResponse(
        AuthLoginResponse authResponse,
        User user,
        IReadOnlyCollection<VanSalesShopDto> shops,
        string? assignedBusinessPartnerName = null)
    {
        var assignedWarehouseCodes = user.GetWarehouseCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToList();

        var assignedCustomerCodes = shops
            .Where(shop => !string.IsNullOrWhiteSpace(shop.Code))
            .Select(shop => shop.Code.Trim())
            .ToList();

        var expiresIn = (int)Math.Max(0, Math.Round((authResponse.ExpiresAt - DateTime.UtcNow).TotalSeconds));

        return new VanSalesLoginResponse
        {
            User = new VanSalesLoginUserDto
            {
                Id = EncodeCompatibilityId(user.Id.ToString()),
                Name = user.FirstName ?? string.Empty,
                Surname = user.LastName ?? string.Empty,
                Email = user.Email ?? string.Empty,
                Branch = ResolveBranch(user, null),
                Role = user.Role,
                Status = user.IsActive ? 1 : 0,
                AssignedSection = user.AssignedSection,
                AssignedWarehouseCode = assignedWarehouseCodes.FirstOrDefault(),
                AssignedWarehouseCodes = assignedWarehouseCodes,
                AssignedCustomerCodes = assignedCustomerCodes,
                AssignedBusinessPartnerCode = user.AssignedBusinessPartnerCode,
                AssignedBusinessPartnerName = assignedBusinessPartnerName ?? string.Empty,
                AssignedCostCentreCode = user.AssignedCostCentreCode,
                SupplyingWarehouseCode = ResolveSupplyingWarehouseCode(user)
            },
            Token = authResponse.AccessToken,
            Shop = shops.ToList(),
            Type = authResponse.TokenType,
            ExpiresIn = expiresIn,
            Rate = LegacyVatRate,
            RefreshToken = authResponse.RefreshToken,
            ExpiresAt = authResponse.ExpiresAt
        };
    }

    public static VanSalesShopDto MapShop(User user, string cardCode, BusinessPartnerDto? partner)
    {
        var addressParts = new[] { partner?.Address, partner?.City, partner?.Country }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());

        return new VanSalesShopDto
        {
            Id = EncodeCompatibilityId(cardCode),
            Code = cardCode,
            Name = partner?.CardName ?? cardCode,
            Currency = partner?.Currency ?? string.Empty,
            Phone = partner?.Phone1 ?? partner?.Phone2 ?? string.Empty,
            Email = partner?.Email ?? string.Empty,
            Address = string.Join(", ", addressParts),
            BpNumber = cardCode,
            VatNumber = partner?.VatRegNo ?? partner?.TinNumber ?? string.Empty,
            PriceList = partner?.PriceListName ?? partner?.PriceListNum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Branch = ResolveBranch(user, partner),
            Status = partner is null || partner.IsActive ? 1 : 0,
            CreatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    public static VanSalesShopDto MapShop(User user, RouteCustomerEntity customer, BusinessPartnerDto? partner)
    {
        var assignedBusinessPartnerCode = user.AssignedBusinessPartnerCode?.Trim() ?? string.Empty;

        return new VanSalesShopDto
        {
            Id = EncodeCompatibilityId(customer.Code),
            Code = customer.Code,
            Name = customer.Name,
            Currency = partner?.Currency ?? string.Empty,
            Phone = customer.Phone ?? string.Empty,
            Email = customer.Email ?? string.Empty,
            Address = customer.Address ?? string.Empty,
            BpNumber = assignedBusinessPartnerCode,
            VatNumber = customer.VatNumber ?? string.Empty,
            PriceList = partner?.PriceListName ?? partner?.PriceListNum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Branch = ResolveBranch(user, partner),
            Status = customer.IsActive ? 1 : 0,
            CreatedAt = customer.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Turns an online van sale into the invoice request that posts it to SAP.
    /// </summary>
    /// <remarks>
    /// <b><see cref="CreateDesktopInvoiceRequest.Fiscalize"/> is off whenever the handset stamped for
    /// itself.</b> A handset owns one ZIMRA device and every receipt on it is chained onto the one
    /// before, so the device must have exactly one writer. Letting the server fiscalise a sale the
    /// handset has already numbered puts a second signature on that chain, and FDMS refuses the whole
    /// fiscal day when the offline file is uploaded — not this receipt, the day. The receipt the handset
    /// signed reaches ZIMRA the only way it can: stored on the sale and drained to the platform by
    /// <c>VanSalesSignedReceiptIngestService</c>.
    ///
    /// <para>
    /// A handset too old to stamp owns no device and holds no chain, so there is nothing to fork and the
    /// server still fiscalises its sales exactly as before. That is the only case left where it does.
    /// </para>
    /// </remarks>
    public static CreateDesktopInvoiceRequest MapInvoiceRequest(
        VanSalesOrderRequest request,
        VanSalesCustomerResolution customer,
        string warehouseCode,
        string costCentreCode)
    {
        return new CreateDesktopInvoiceRequest
        {
            ExternalReferenceId = request.VanOrder,
            SourceSystem = "KefalosVanSales",
            CardCode = customer.PostingCardCode,
            // The shop, carried alongside the account being billed. `ref` is whatever the handset chose to
            // label the sale with and is not a substitute — only these three say who bought.
            CardName = request.Reference,
            RouteCustomerId = customer.RouteCustomerId,
            RouteCustomerCode = customer.RouteCustomerCode,
            RouteCustomerName = customer.RouteCustomerName,
            DocDate = NormalizeDocumentDate(request.DueDate),
            DocDueDate = NormalizeDocumentDate(request.DueDate),
            NumAtCard = request.VanOrder,
            Comments = BuildInvoiceComments(request),
            DocCurrency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim(),
            // Null from a handset that predates the payment step. Left null rather than defaulted to
            // cash: an assumed tender in a cash-control report is worse than an absent one.
            PaymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod)
                ? null
                : request.PaymentMethod.Trim(),
            Fiscalize = !request.ClaimsReceiptSequence(),
            Lines = request.Items.Select((item, index) => MapServerAllocatedInvoiceLine(
                item,
                index,
                warehouseCode,
                costCentreCode)).ToList()
        };
    }

    /// <summary>
    /// Turns an existing van sales order into the invoice conversion request.
    /// </summary>
    /// <remarks>
    /// <see cref="ConvertSalesOrderToInvoiceRequest.Fiscalize"/> stays unconditionally true here, unlike
    /// <see cref="MapInvoiceRequest"/>, and that is safe only because
    /// <c>ConvertVanSalesSalesOrderToInvoiceHandler</c> refuses a request carrying a signed receipt before
    /// reaching this method. This path stores no receipt, so a stamped sale must never get here — see the
    /// guard there for what it would cost.
    /// </remarks>
    public static ConvertSalesOrderToInvoiceRequest MapConvertRequest(
        VanSalesOrderRequest request,
        int salesOrderId,
        string warehouseCode,
        string costCentreCode)
    {
        return new ConvertSalesOrderToInvoiceRequest
        {
            SalesOrderId = salesOrderId,
            ExternalReferenceId = request.VanOrder,
            SourceSystem = "KefalosVanSales",
            DocDate = NormalizeDocumentDate(request.DueDate),
            DocDueDate = NormalizeDocumentDate(request.DueDate),
            NumAtCard = request.VanOrder,
            Comments = string.IsNullOrWhiteSpace(request.SalesOrder)
                ? $"Van sales conversion {request.VanOrder}".Trim()
                : $"Van sales conversion from {request.SalesOrder}".Trim(),
            DocCurrency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim(),
            PaymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod)
                ? null
                : request.PaymentMethod.Trim(),
            Fiscalize = true,
            Lines = request.Items.Count == 0
                ? null
                : request.Items.Select((item, index) => MapServerAllocatedInvoiceLine(
                    item,
                    index,
                    warehouseCode,
                    costCentreCode)).ToList()
        };
    }

    private static CreateDesktopInvoiceLineRequest MapServerAllocatedInvoiceLine(
        VanSalesOrderItemRequest item,
        int index,
        string warehouseCode,
        string costCentreCode)
    {
        return new CreateDesktopInvoiceLineRequest
        {
            LineNum = index,
            ItemCode = item.Code.Trim(),
            Quantity = item.Quantity,
            UnitPrice = Convert.ToDecimal(item.Price, CultureInfo.InvariantCulture),
            WarehouseCode = warehouseCode,
            CostCentreCode = costCentreCode,
            AutoAllocateBatches = true,
            BatchNumbers = null
        };
    }

    /// <summary>
    /// A van's stock request: from the depot it is loaded at, to the van itself.
    /// </summary>
    /// <remarks>
    /// The source is passed in from the account's assignment rather than read off the request. The
    /// handset's own warehouse field named a warehouse in words ("Graniteside Center"), which is not a
    /// code SAP knows, and a van's depot is fixed anyway — so whatever it sends there is ignored.
    /// </remarks>
    public static CreateDesktopTransferRequestDto MapTransferRequest(
        VanSalesTransferRequest request,
        User user,
        string destinationWarehouseCode,
        string sourceWarehouseCode)
    {
        var requesterName = string.Join(" ", new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

        return new CreateDesktopTransferRequestDto
        {
            FromWarehouse = sourceWarehouseCode,
            ToWarehouse = destinationWarehouseCode,
            DocDate = NormalizeDocumentDate(request.DocDate),
            DueDate = NormalizeDocumentDate(request.DocDate),
            Comments = string.IsNullOrWhiteSpace(request.Remarks)
                ? $"Van sales stock request {request.DocDate}".Trim()
                : request.Remarks.Trim(),
            RequesterEmail = user.Email,
            RequesterName = string.IsNullOrWhiteSpace(requesterName) ? user.Username : requesterName,
            RequesterBranch = request.Branch > 0 ? request.Branch : null,
            Lines = request.Items.Select(item => new CreateDesktopTransferRequestLineDto
            {
                ItemCode = item.Code.Trim(),
                Quantity = item.Quantity ?? 0,
                FromWarehouseCode = sourceWarehouseCode,
                ToWarehouseCode = destinationWarehouseCode
            }).ToList()
        };
    }

    /// <summary>
    /// Answers the handset, naming the fiscal receipt the sale carries.
    /// </summary>
    /// <remarks>
    /// <paramref name="stampedBy"/> is the sale's own signed receipt, when it had one. The server does
    /// not fiscalise a stamped sale — see <see cref="MapInvoiceRequest"/> — so there is no platform
    /// result to report, and reporting nothing would have the handset show its own printed receipt as
    /// "Not Fiscalised". What it gets back instead is what it sent, which is the truth: that receipt is
    /// the sale's fiscal record and the server has taken custody of it.
    /// </remarks>
    public static VanSalesDirectInvoiceResponse MapInvoiceResponse(
        ConfirmReservationResponseDto response,
        string externalReference,
        VanSalesOrderRequest? stampedBy = null)
    {
        var handsetReceipt = stampedBy is not null && stampedBy.ClaimsReceiptSequence() ? stampedBy : null;

        return new VanSalesDirectInvoiceResponse
        {
            Success = response.Success,
            Message = response.Message,
            ExternalReference = externalReference,
            ReservationId = response.ReservationId,
            SapDocEntry = response.SAPDocEntry,
            SapDocNum = response.SAPDocNum,
            WasQueued = response.WasQueued,
            QueueId = response.QueueId,
            QueueStatus = response.QueueStatus,
            QueueExternalReference = response.QueueExternalReference,
            EstimatedProcessingSeconds = response.EstimatedProcessingSeconds,
            StatusUrl = !string.IsNullOrWhiteSpace(response.StatusUrl)
                ? response.StatusUrl
                : response.WasQueued && !string.IsNullOrWhiteSpace(response.ReservationId)
                    ? $"/api/DesktopIntegration/queue/by-reservation/{Uri.EscapeDataString(response.ReservationId)}"
                    : null,
            VerificationCode = handsetReceipt?.VerificationCode ?? response.Fiscalization?.VerificationCode,
            QrCode = handsetReceipt?.QrCode ?? response.Fiscalization?.QRCode,
            FiscalDay = handsetReceipt?.FiscalDayNo?.ToString(CultureInfo.InvariantCulture)
                        ?? response.Fiscalization?.FiscalDayNo,
            ReceiptGlobalNo = handsetReceipt?.ReceiptGlobalNo?.ToString(CultureInfo.InvariantCulture)
                              ?? response.Fiscalization?.ReceiptGlobalNo,
            DeviceSerial = handsetReceipt?.FiscalDeviceId ?? response.Fiscalization?.DeviceSerial,
            Errors = response.Errors
        };
    }

    public static VanSalesConvertSalesOrderToInvoiceResponse MapConvertResponse(ConvertSalesOrderToInvoiceResponseDto response)
    {
        return new VanSalesConvertSalesOrderToInvoiceResponse
        {
            Success = response.Success,
            Message = response.Message,
            SalesOrderId = response.SalesOrderId,
            SalesOrderNumber = response.SalesOrderNumber,
            ExternalReference = response.ExternalReference,
            ReservationId = response.ReservationId,
            QueueId = response.QueueId,
            Status = response.Status,
            EstimatedProcessingSeconds = response.EstimatedProcessingSeconds,
            StatusUrl = !string.IsNullOrWhiteSpace(response.StatusUrl)
                ? response.StatusUrl
                : !string.IsNullOrWhiteSpace(response.ExternalReference)
                    ? $"/api/DesktopIntegration/queue/{Uri.EscapeDataString(response.ExternalReference)}"
                    : null,
            Errors = response.Errors
        };
    }

    public static VanSalesTransferRequestResponse MapTransferResponse(InventoryTransferRequestDto response)
    {
        return new VanSalesTransferRequestResponse
        {
            Success = true,
            Message = $"Transfer request {response.DocNum} created successfully",
            DocEntry = response.DocEntry,
            DocNum = response.DocNum
        };
    }

    public static CreateSalesOrderRequest MapSalesOrderRequest(
        VanSalesOrderRequest request,
        VanSalesCustomerResolution customer,
        string warehouseCode,
        string costCentreCode)
    {
        var cardCode = customer.PostingCardCode;

        return new CreateSalesOrderRequest
        {
            DeliveryDate = ParseLegacyDate(request.DueDate),
            CardCode = cardCode,
            CardName = string.IsNullOrWhiteSpace(request.Reference) ? cardCode : request.Reference.Trim(),
            RouteCustomerId = customer.RouteCustomerId,
            RouteCustomerCode = customer.RouteCustomerCode,
            RouteCustomerName = customer.RouteCustomerName,
            CustomerRefNo = string.IsNullOrWhiteSpace(request.VanOrder) ? null : request.VanOrder.Trim(),
            Comments = string.IsNullOrWhiteSpace(request.VanOrder)
                ? "Van sales sales order"
                : $"Van sales sales order {request.VanOrder.Trim()}",
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim(),
            WarehouseCode = warehouseCode,
            Source = SalesOrderSource.Mobile,
            ClientRequestId = string.IsNullOrWhiteSpace(request.VanOrder) ? null : request.VanOrder.Trim(),
            Latitude = ParseCoordinate(request.Latitude),
            Longitude = ParseCoordinate(request.Longitude),
            Lines = request.Items.Select(item => new CreateSalesOrderLineRequest
            {
                ItemCode = item.Code.Trim(),
                Quantity = item.Quantity,
                UnitPrice = Convert.ToDecimal(item.Price, CultureInfo.InvariantCulture),
                WarehouseCode = warehouseCode,
                CostCentreCode = costCentreCode,
                BatchNumber = item.Batches.Count == 1 ? item.Batches[0].Batch.Trim() : null
            }).ToList()
        };
    }

    public static VanSalesLegacyOrderDto MapLegacySalesOrder(SalesOrderDto order)
    {
        var netTotal = Math.Max(order.DocTotal - order.TaxAmount, 0m);

        return new VanSalesLegacyOrderDto
        {
            Id = order.Id,
            CustomerId = EncodeCompatibilityId(order.CardCode ?? string.Empty),
            Reference = order.CardName ?? order.CardCode ?? order.OrderNumber,
            Type = "SO",
            Currency = order.Currency ?? "USD",
            Item = order.Lines.Count,
            Units = order.Lines.Sum(line => RoundLegacyQuantity(line.Quantity)),
            Price = ToLegacyDouble(netTotal),
            DocDate = FormatLegacyDateTime(order.OrderDate),
            DueDate = FormatLegacyDateTime(order.DeliveryDate ?? order.OrderDate),
            Invoice = order.InvoiceSapDocNum?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            DocNum = order.SAPDocNum?.ToString(CultureInfo.InvariantCulture) ?? order.OrderNumber,
            DocEntry = order.SAPDocEntry?.ToString(CultureInfo.InvariantCulture) ?? order.Id.ToString(CultureInfo.InvariantCulture),
            PurchaseOrders = order.OrderNumber,
            Fiscalized = 0,
            Verification = string.Empty,
            QrCode = string.Empty,
            Status = (int)order.Status,
            Timestamps = new VanSalesLegacyTimestampsDto
            {
                CreateDate = FormatLegacyDateTime(order.CreatedAt),
                ApprovalDate = FormatLegacyDateTime(order.ApprovedDate),
                DeliveryDate = FormatLegacyDateTime(order.DeliveryDate)
            },
            Pod = new VanSalesLegacyPodDto(),
            OrderItems = order.Lines.Select(line => new VanSalesLegacyOrderItemDto
            {
                OrderId = order.Id,
                Name = line.ItemDescription ?? line.ItemCode,
                Code = line.ItemCode,
                Quantity = RoundLegacyQuantity(line.Quantity),
                Price = ToLegacyDouble(line.UnitPrice),
                PriceTotal = ToLegacyDouble(line.LineTotal)
            }).ToList(),
            FiscalizedText = "Not Fiscalised",
            FiscalizedTextColor = "Black"
        };
    }

    public static VanSalesLegacyOrderDto MapLegacyInvoice(
        Invoice invoice,
        DesktopFiscalTransactionEntity? fiscalTransaction)
    {
        var lines = (invoice.DocumentLines ?? new List<InvoiceLine>())
            .OrderBy(line => line.LineNum)
            .ToList();

        var docDate = ParseLegacyDate(invoice.DocDate);
        var dueDate = ParseLegacyDate(invoice.DocDueDate) ?? docDate;
        var createdAt = fiscalTransaction?.TimestampUtc ?? docDate;
        var netTotal = Math.Max(invoice.DocTotal - invoice.VatSum, 0m);
        var isFiscalized = fiscalTransaction is not null &&
            (string.Equals(fiscalTransaction.Status, "Success", StringComparison.OrdinalIgnoreCase) ||
             !string.IsNullOrWhiteSpace(fiscalTransaction.VerificationCode) ||
             !string.IsNullOrWhiteSpace(fiscalTransaction.QRCode) ||
             fiscalTransaction.ReceiptGlobalNo.HasValue);

        return new VanSalesLegacyOrderDto
        {
            Id = invoice.DocEntry,
            CustomerId = EncodeCompatibilityId(invoice.CardCode ?? string.Empty),
            Reference = invoice.NumAtCard
                ?? invoice.U_Van_saleorder
                ?? invoice.CardName
                ?? invoice.CardCode
                ?? invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            Type = "INV",
            Currency = invoice.DocCurrency ?? "USD",
            Item = lines.Count,
            Units = lines.Sum(line => RoundLegacyQuantity(line.Quantity)),
            Price = ToLegacyDouble(netTotal),
            DocDate = FormatLegacyDateTime(docDate),
            DueDate = FormatLegacyDateTime(dueDate),
            Invoice = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            DocNum = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            DocEntry = invoice.DocEntry.ToString(CultureInfo.InvariantCulture),
            PurchaseOrders = invoice.U_Van_saleorder ?? invoice.NumAtCard ?? string.Empty,
            Fiscalized = isFiscalized ? 1 : 0,
            Verification = fiscalTransaction?.VerificationCode ?? string.Empty,
            QrCode = fiscalTransaction?.QRCode ?? string.Empty,
            Status = isFiscalized ? 2 : 0,
            Timestamps = new VanSalesLegacyTimestampsDto
            {
                CreateDate = FormatLegacyDateTime(createdAt),
                ApprovalDate = FormatLegacyDateTime(fiscalTransaction?.TimestampUtc),
                DeliveryDate = string.Empty
            },
            Pod = new VanSalesLegacyPodDto(),
            OrderItems = lines.Select(line =>
            {
                var unitPrice = line.UnitPrice > 0m ? line.UnitPrice : line.Price;
                var lineTotal = line.LineTotal > 0m ? line.LineTotal : unitPrice * line.Quantity;

                return new VanSalesLegacyOrderItemDto
                {
                    OrderId = invoice.DocEntry,
                    Name = line.ItemDescription ?? line.ItemCode ?? string.Empty,
                    Code = line.ItemCode ?? string.Empty,
                    Quantity = RoundLegacyQuantity(line.Quantity),
                    Price = ToLegacyDouble(unitPrice),
                    PriceTotal = ToLegacyDouble(lineTotal)
                };
            }).ToList(),
            FiscalizedText = isFiscalized ? "Fiscalised" : "Not Fiscalised",
            FiscalizedTextColor = isFiscalized ? "Green" : "Black"
        };
    }

    public static VanSalesLegacyInventoryOrderDto MapLegacyTransferRequest(
        InventoryTransferRequestDto request,
        int status)
    {
        var requestDate = ParseLegacyDate(request.DocDate) ?? ParseLegacyDate(request.DueDate);

        return new VanSalesLegacyInventoryOrderDto
        {
            Id = request.DocEntry,
            User = 0,
            Branch = request.RequesterBranch ?? 0,
            Warehouse = request.ToWarehouse ?? request.FromWarehouse ?? string.Empty,
            Date = FormatLegacyDateTime(requestDate),
            Remarks = request.Comments ?? string.Empty,
            DocDate = FormatLegacyDateTime(requestDate),
            DocEntry = request.DocEntry,
            DocNum = request.DocNum,
            Status = status,
            Items = (request.Lines ?? new List<InventoryTransferRequestLineDto>())
                .OrderBy(line => line.LineNum)
                .Select(line => new VanSalesLegacyInventoryOrderItemDto
                {
                    Code = line.ItemCode ?? string.Empty,
                    Quantity = RoundLegacyQuantity(line.Quantity),
                    Price = 0d,
                    Warehouse = line.ToWarehouseCode ?? request.ToWarehouse ?? request.FromWarehouse ?? string.Empty,
                    Product = new VanSalesLegacyInventoryProductDto
                    {
                        Code = line.ItemCode ?? string.Empty,
                        Name = line.ItemDescription ?? line.ItemCode ?? string.Empty,
                        Category = string.Empty,
                        Price = 0d,
                        PriceZig = 0d,
                        Quantity = RoundLegacyQuantity(line.Quantity),
                        PricesList = new List<object>()
                    }
                })
                .ToList()
        };
    }

    public static VanSalesLegacyFiscalDto MapLegacyFiscal(DesktopFiscalTransactionEntity? transaction)
    {
        if (transaction is null)
        {
            return new VanSalesLegacyFiscalDto();
        }

        return new VanSalesLegacyFiscalDto
        {
            Id = transaction.Id,
            Status = transaction.Status,
            VerificationCode = transaction.VerificationCode ?? string.Empty,
            VerificationLink = string.Empty,
            DeviceId = transaction.DeviceId ?? string.Empty,
            DeviceSerialNumber = transaction.DeviceSerialNumber ?? string.Empty,
            FiscalDay = int.TryParse(transaction.FiscalDay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fiscalDay)
                ? fiscalDay
                : 0,
            CreatedAt = AuditService.ToCAT(transaction.TimestampUtc),
            UpdatedAt = AuditService.ToCAT(transaction.LastSyncedAtUtc)
        };
    }

    /// <summary>
    /// Whether a request names this customer, by code or by the hashed id the legacy app was given.
    ///
    /// The handset sends whichever it holds — newer builds send <c>customer_code</c>, older ones only the
    /// encoded <c>customer</c> id — so both are accepted rather than one being assumed.
    /// </summary>
    public static bool MatchesRequestedCustomer(VanSalesOrderRequest request, string code)
    {
        if (!string.IsNullOrWhiteSpace(request.CustomerCode) &&
            string.Equals(request.CustomerCode.Trim(), code, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return EncodeCompatibilityId(code) == request.Customer;
    }

    public static int EncodeCompatibilityId(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var numeric = BitConverter.ToUInt32(hash, 0) & 0x7FFFFFFF;
        return numeric == 0 ? 1 : (int)numeric;
    }

    public static int? ParseSalesOrderId(VanSalesOrderRequest request)
    {
        if (request.SalesOrderId.HasValue && request.SalesOrderId.Value > 0)
        {
            return request.SalesOrderId.Value;
        }

        if (string.IsNullOrWhiteSpace(request.SalesOrder))
        {
            return null;
        }

        var match = TrailingDigitsRegex.Match(request.SalesOrder.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out var salesOrderId)
            ? salesOrderId
            : null;
    }

    public static string? ResolveAssignedWarehouseCode(User user)
    {
        return user.GetWarehouseCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .FirstOrDefault()
            ?? user.AssignedWarehouseCode?.Trim();
    }

    public static string? ResolveAssignedCostCentreCode(User user)
    {
        return string.IsNullOrWhiteSpace(user.AssignedCostCentreCode)
            ? null
            : user.AssignedCostCentreCode.Trim();
    }

    /// <summary>
    /// The depot the van is loaded from. Null when the account has not been assigned one — there is
    /// nothing sensible to guess: sending a Bulawayo van's request to the Harare depot would have it
    /// picked and packed 440km from the van waiting for it.
    /// </summary>
    public static string? ResolveSupplyingWarehouseCode(User user)
    {
        return string.IsNullOrWhiteSpace(user.SupplyingWarehouseCode)
            ? null
            : user.SupplyingWarehouseCode.Trim();
    }

    /// <summary>
    /// Reads one of the date strings the legacy handset and SAP exchange.
    /// </summary>
    /// <remarks>
    /// The answer is always a CAT wall clock carrying <see cref="DateTimeKind.Unspecified"/>, because
    /// that is all these strings ever hold: "2026-08-12" is a trading day in the van's own clock, not
    /// an instant, and the handset has no notion of UTC to tell us otherwise.
    ///
    /// <para>
    /// It parsed with <see cref="DateTimeStyles.AssumeLocal"/> until 2026-08-12, which stamped the
    /// server's zone onto a value that never had one. Npgsql refuses a Local kind against
    /// <c>timestamp with time zone</c> outright, so both van sales history endpoints answered 500 to
    /// every refresh a handset made. Anything comparing one of these against such a column has to
    /// convert it first — see <see cref="VanSalesLegacyDateWindow"/>, which is the only correct way to
    /// do it.
    /// </para>
    /// </remarks>
    public static DateTime? ParseLegacyDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var formats = new[]
        {
            "yyyy/MM/dd",
            "yyyy-MM-dd",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "O"
        };

        if (DateTime.TryParseExact(
            trimmed,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var exactDate))
        {
            return AsCatWallClock(exactDate);
        }

        return DateTime.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedDate)
            ? AsCatWallClock(parsedDate)
            : null;
    }

    /// <summary>
    /// Reduces a parsed value to the CAT wall clock the rest of this mapper works in.
    /// </summary>
    /// <remarks>
    /// Only the round-trip ("O") form carries a zone, and .NET hands one of those back already moved
    /// to the server's own. That is a real instant, so it is converted to CAT rather than read as if
    /// its digits were CAT already. Every other format arrives unzoned and is what it says it is.
    /// </remarks>
    private static DateTime AsCatWallClock(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? value
            : DateTime.SpecifyKind(AuditService.ToCAT(value), DateTimeKind.Unspecified);

    /// <summary>
    /// Renders a handset-supplied date as the ISO string SAP will accept.
    /// </summary>
    /// <remarks>
    /// This trimmed a supplied value and passed it on, which normalized nothing: handsets send
    /// yyyy/MM/dd, and on the transfer path that reached SAP untouched and came back rejected. An
    /// unparseable value is still forwarded as sent, so the caller sees SAP's own complaint about
    /// the date rather than a mapping error inventing one.
    /// </remarks>
    private static string NormalizeDocumentDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var trimmed = value.Trim();

        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : trimmed;
    }

    private static string BuildInvoiceComments(VanSalesOrderRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SalesOrder))
        {
            return $"Van sales invoice for {request.SalesOrder}";
        }

        return string.IsNullOrWhiteSpace(request.VanOrder)
            ? "Van sales direct invoice"
            : $"Van sales direct invoice {request.VanOrder}";
    }

    private static string ResolveBranch(User user, BusinessPartnerDto? partner)
    {
        var assignedWarehouseCode = ResolveAssignedWarehouseCode(user);

        if (string.Equals(user.Role, "ADR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(user.Role, "Sales", StringComparison.OrdinalIgnoreCase))
        {
            return assignedWarehouseCode
                ?? user.AssignedSection?.Trim()
                ?? partner?.Channel?.Trim()
                ?? string.Empty;
        }

        return user.AssignedSection?.Trim()
            ?? partner?.Channel?.Trim()
            ?? assignedWarehouseCode
            ?? string.Empty;
    }

    private static decimal? ParseCoordinate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Renders a date for the handset, which reads everything as CAT and has no way to say otherwise.
    /// </summary>
    /// <remarks>
    /// Both kinds of value arrive here. Instants out of the database — a fiscal timestamp, an order's
    /// CreatedAt — are UTC and have to be moved. CAT wall clocks that came off a legacy date string
    /// through <see cref="ParseLegacyDate"/> are already in the handset's terms, and converting one
    /// again would add the CAT offset a second time and show a trading day starting at 02:00.
    /// </remarks>
    private static string FormatLegacyDateTime(DateTime? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }

        var catValue = value.Value.Kind == DateTimeKind.Unspecified
            ? value.Value
            : AuditService.ToCAT(value.Value);

        return catValue.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static int RoundLegacyQuantity(decimal value)
    {
        return Convert.ToInt32(decimal.Round(value, 0, MidpointRounding.AwayFromZero));
    }

    private static double ToLegacyDouble(decimal value)
    {
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }
}