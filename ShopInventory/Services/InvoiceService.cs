using ShopInventory.DTOs;

namespace ShopInventory.Services
{
    public interface IInvoiceService
    {
        Task<List<InvoiceDto>> GetInvoicesByCustomerAsync(string cardCode);
    }

    public class InvoiceService : IInvoiceService
    {
        private readonly ISAPServiceLayerClient _sapClient;
        private readonly ILogger<InvoiceService> _logger;

        public InvoiceService(ISAPServiceLayerClient sapClient, ILogger<InvoiceService> logger)
        {
            _sapClient = sapClient;
            _logger = logger;
        }

        public async Task<List<InvoiceDto>> GetInvoicesByCustomerAsync(string cardCode)
        {
            try
            {
                var invoices = await _sapClient.GetInvoicesByCustomerAsync(cardCode);
                return invoices.Select(i => new InvoiceDto
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
                    DocDate = i.DocDate,
                    DocDueDate = i.DocDueDate,
                    CardCode = i.CardCode,
                    CardName = i.CardName,
                    DocTotal = i.DocTotal,
                    DocCurrency = i.DocCurrency,
                    Comments = i.Comments,
                    Remarks = i.Comments
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching invoices for {CardCode}", cardCode);
                return new List<InvoiceDto>();
            }
        }
    }
}
