using ShopInventory.DTOs;

namespace ShopInventory.Services
{
    public interface IIncomingPaymentService
    {
        Task<List<IncomingPaymentDto>> GetIncomingPaymentsByCustomerAsync(string cardCode);
        Task<IncomingPaymentDto?> GetIncomingPaymentAsync(int docEntry);
        Task<IncomingPaymentListResponseDto?> GetAllIncomingPaymentsAsync(int limit = 100);
    }

    public class IncomingPaymentService : IIncomingPaymentService
    {
        private readonly ISAPServiceLayerClient _sapClient;
        private readonly ILogger<IncomingPaymentService> _logger;

        public IncomingPaymentService(ISAPServiceLayerClient sapClient, ILogger<IncomingPaymentService> logger)
        {
            _sapClient = sapClient;
            _logger = logger;
        }

        public async Task<List<IncomingPaymentDto>> GetIncomingPaymentsByCustomerAsync(string cardCode)
        {
            try
            {
                var payments = await _sapClient.GetIncomingPaymentsByCustomerAsync(cardCode);
                return payments.Select(p => new IncomingPaymentDto
                {
                    DocEntry = p.DocEntry,
                    DocNum = p.DocNum,
                    DocDate = p.DocDate?.ToString(),
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    // Calculate total from payment method sums if DocTotal is 0
                    DocTotal = p.DocTotal > 0 ? p.DocTotal : p.CashSum + p.TransferSum + p.CheckSum + p.CreditSum,
                    DocCurrency = p.DocCurrency,
                    Remarks = p.Remarks,
                    CashSum = p.CashSum,
                    TransferSum = p.TransferSum,
                    CheckSum = p.CheckSum,
                    CreditSum = p.CreditSum,
                    TransferReference = p.TransferReference
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching incoming payments for {CardCode}", cardCode);
                return new List<IncomingPaymentDto>();
            }
        }

        public async Task<IncomingPaymentDto?> GetIncomingPaymentAsync(int docEntry)
        {
            try
            {
                var payment = await _sapClient.GetIncomingPaymentByDocEntryAsync(docEntry);
                if (payment == null) return null;

                return new IncomingPaymentDto
                {
                    DocEntry = payment.DocEntry,
                    DocNum = payment.DocNum,
                    DocDate = payment.DocDate?.ToString(),
                    CardCode = payment.CardCode,
                    CardName = payment.CardName,
                    // Calculate total from payment method sums if DocTotal is 0
                    DocTotal = payment.DocTotal > 0 ? payment.DocTotal : payment.CashSum + payment.TransferSum + payment.CheckSum + payment.CreditSum,
                    DocCurrency = payment.DocCurrency,
                    Remarks = payment.Remarks,
                    CashSum = payment.CashSum,
                    TransferSum = payment.TransferSum,
                    CheckSum = payment.CheckSum,
                    CreditSum = payment.CreditSum,
                    TransferReference = payment.TransferReference
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching incoming payment {DocEntry}", docEntry);
                return null;
            }
        }

        public async Task<IncomingPaymentListResponseDto?> GetAllIncomingPaymentsAsync(int limit = 100)
        {
            try
            {
                var payments = await _sapClient.GetIncomingPaymentsAsync();
                var limitedPayments = payments.Take(limit).Select(p => new IncomingPaymentDto
                {
                    DocEntry = p.DocEntry,
                    DocNum = p.DocNum,
                    DocDate = p.DocDate?.ToString(),
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    // Calculate total from payment method sums if DocTotal is 0
                    DocTotal = p.DocTotal > 0 ? p.DocTotal : p.CashSum + p.TransferSum + p.CheckSum + p.CreditSum,
                    DocCurrency = p.DocCurrency,
                    Remarks = p.Remarks,
                    CashSum = p.CashSum,
                    TransferSum = p.TransferSum,
                    CheckSum = p.CheckSum,
                    CreditSum = p.CreditSum,
                    TransferReference = p.TransferReference
                }).ToList();

                return new IncomingPaymentListResponseDto
                {
                    Count = payments.Count,
                    Payments = limitedPayments
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching all incoming payments");
                return null;
            }
        }
    }
}
