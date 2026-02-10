using ShopInventory.DTOs;

namespace ShopInventory.Services
{
    public interface IPaymentService
    {
        Task<List<PaymentDto>> GetPaymentsByCustomerAsync(string cardCode);
    }

    public class PaymentService : IPaymentService
    {
        private readonly ISAPServiceLayerClient _sapClient;
        private readonly ILogger<PaymentService> _logger;

        public PaymentService(ISAPServiceLayerClient sapClient, ILogger<PaymentService> logger)
        {
            _sapClient = sapClient;
            _logger = logger;
        }

        public async Task<List<PaymentDto>> GetPaymentsByCustomerAsync(string cardCode)
        {
            try
            {
                // Get incoming payments by customer as they represent vendor payments
                var payments = await _sapClient.GetIncomingPaymentsByCustomerAsync(cardCode);
                return payments.Select(p => new PaymentDto
                {
                    DocEntry = p.DocEntry,
                    DocNum = p.DocNum,
                    DocDate = p.DocDate?.ToString(),
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    DocTotal = p.DocTotal,
                    DocCurrency = p.DocCurrency,
                    Remarks = p.Remarks
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching payments for {CardCode}", cardCode);
                return new List<PaymentDto>();
            }
        }
    }
}
