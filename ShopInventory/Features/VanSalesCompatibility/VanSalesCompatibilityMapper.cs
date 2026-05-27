using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility;

public static partial class VanSalesCompatibilityMapper
{
    private const int LegacyVatRate = 15;
    private static readonly Regex TrailingDigitsRegex = new("(\\d+)$", RegexOptions.Compiled);

    public static VanSalesLoginResponse MapLoginResponse(
        AuthLoginResponse authResponse,
        User user,
        IReadOnlyCollection<VanSalesShopDto> shops)
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
                AssignedCostCentreCode = user.AssignedCostCentreCode
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

    public static CreateDesktopInvoiceRequest MapInvoiceRequest(
        VanSalesOrderRequest request,
        string cardCode,
        string warehouseCode,
        string costCentreCode)
    {
        return new CreateDesktopInvoiceRequest
        {
            ExternalReferenceId = request.VanOrder,
            SourceSystem = "KefalosVanSales",
            CardCode = cardCode,
            CardName = request.Reference,
            DocDate = NormalizeDocumentDate(request.DueDate),
            DocDueDate = NormalizeDocumentDate(request.DueDate),
            NumAtCard = request.VanOrder,
            Comments = BuildInvoiceComments(request),
            DocCurrency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim(),
            Fiscalize = true,
            Lines = request.Items.Select((item, index) => new CreateDesktopInvoiceLineRequest
            {
                LineNum = index,
                ItemCode = item.Code.Trim(),
                Quantity = item.Quantity,
                UnitPrice = Convert.ToDecimal(item.Price, CultureInfo.InvariantCulture),
                WarehouseCode = warehouseCode,
                CostCentreCode = costCentreCode,
                AutoAllocateBatches = item.Batches.Count == 0,
                BatchNumbers = item.Batches.Count == 0
                    ? null
                    : item.Batches.Select(batch => new DesktopBatchRequest
                    {
                        BatchNumber = batch.Batch.Trim(),
                        Quantity = batch.Quantity
                    }).ToList()
            }).ToList()
        };
    }

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
            Fiscalize = true,
            Lines = request.Items.Count == 0
                ? null
                : request.Items.Select((item, index) => new CreateDesktopInvoiceLineRequest
                {
                    LineNum = index,
                    ItemCode = item.Code.Trim(),
                    Quantity = item.Quantity,
                    UnitPrice = Convert.ToDecimal(item.Price, CultureInfo.InvariantCulture),
                    WarehouseCode = warehouseCode,
                    CostCentreCode = costCentreCode,
                    AutoAllocateBatches = item.Batches.Count == 0,
                    BatchNumbers = item.Batches.Count == 0
                        ? null
                        : item.Batches.Select(batch => new DesktopBatchRequest
                        {
                            BatchNumber = batch.Batch.Trim(),
                            Quantity = batch.Quantity
                        }).ToList()
                }).ToList()
        };
    }

    public static CreateDesktopTransferRequestDto MapTransferRequest(
        VanSalesTransferRequest request,
        User user,
        string destinationWarehouseCode)
    {
        var requesterName = string.Join(" ", new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

        return new CreateDesktopTransferRequestDto
        {
            FromWarehouse = request.Warehouse.Trim(),
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
                FromWarehouseCode = request.Warehouse.Trim(),
                ToWarehouseCode = destinationWarehouseCode
            }).ToList()
        };
    }

    public static VanSalesDirectInvoiceResponse MapInvoiceResponse(
        ConfirmReservationResponseDto response,
        string externalReference)
    {
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
            VerificationCode = response.Fiscalization?.VerificationCode,
            QrCode = response.Fiscalization?.QRCode,
            FiscalDay = response.Fiscalization?.FiscalDayNo,
            ReceiptGlobalNo = response.Fiscalization?.ReceiptGlobalNo,
            DeviceSerial = response.Fiscalization?.DeviceSerial,
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
        string cardCode,
        string warehouseCode,
        string costCentreCode)
    {
        return new CreateSalesOrderRequest
        {
            DeliveryDate = ParseLegacyDate(request.DueDate),
            CardCode = cardCode,
            CardName = string.IsNullOrWhiteSpace(request.Reference) ? cardCode : request.Reference.Trim(),
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
            DateTimeStyles.AssumeLocal,
            out var exactDate))
        {
            return exactDate;
        }

        return DateTime.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsedDate)
            ? parsedDate
            : null;
    }

    private static string NormalizeDocumentDate(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.Trim();
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

    private static string FormatLegacyDateTime(DateTime? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }

        return AuditService.ToCAT(value.Value)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
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