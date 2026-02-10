using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using System;
using System.Threading.Tasks;

namespace ShopInventory.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StatementController : ControllerBase
    {
        private readonly IBusinessPartnerService _businessPartnerService;
        private readonly IInvoiceService _invoiceService;
        private readonly IIncomingPaymentService _incomingPaymentService;
        private readonly IStatementService _statementService;
        private readonly ILogger<StatementController> _logger;

        public StatementController(
            IBusinessPartnerService businessPartnerService,
            IInvoiceService invoiceService,
            IIncomingPaymentService incomingPaymentService,
            IStatementService statementService,
            ILogger<StatementController> logger)
        {
            _businessPartnerService = businessPartnerService;
            _invoiceService = invoiceService;
            _incomingPaymentService = incomingPaymentService;
            _statementService = statementService;
            _logger = logger;
        }

        [HttpGet("generate/{cardCode}")]
        public async Task<IActionResult> GenerateStatement(
            string cardCode,
            [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null)
        {
            try
            {
                // Set default dates if not provided
                var from = fromDate ?? DateTime.Now.AddMonths(-3);
                var to = toDate ?? DateTime.Now;

                // Get customer details
                var customer = await _businessPartnerService.GetBusinessPartnerByCodeAsync(cardCode);
                if (customer == null)
                    return NotFound(new { message = "Customer not found" });

                // Get invoices, payments, and incoming payments for the period
                var invoices = await _invoiceService.GetInvoicesByCustomerAsync(cardCode);
                var incomingPayments = await _incomingPaymentService.GetIncomingPaymentsByCustomerAsync(cardCode);

                // Filter valid invoices (exclude canceled/closed ones)
                var filteredInvoices = invoices
                    .Where(i => IsValidInvoice(i) && FilterByDate(i.DocDate, from, to))
                    .ToList();

                // Filter incoming payments by date range
                var filteredIncomingPayments = incomingPayments
                    .Where(ip => FilterByDate(ip.DocDate, from, to))
                    .OrderBy(p => GetDateValue(p.DocDate))
                    .ToList();

                // Generate PDF
                var pdfBytes = await _statementService.GenerateCustomerStatementAsync(
                    customer, filteredInvoices, filteredIncomingPayments, from, to);

                return File(pdfBytes, "application/pdf",
                    $"Statement_{cardCode}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating statement for {CardCode}", cardCode);
                return StatusCode(500, new { message = "Error generating statement", error = ex.Message });
            }
        }

        private bool IsValidInvoice(InvoiceDto invoice)
        {
            try
            {
                // Exclude canceled invoices
                if (invoice.DocStatus == "X") // Canceled
                    return false;

                // Check if amount is positive
                return invoice.DocTotal >= 0;
            }
            catch
            {
                return true;
            }
        }

        private bool FilterByDate(string? docDate, DateTime fromDate, DateTime toDate)
        {
            try
            {
                var date = GetDateValue(docDate);
                return date >= fromDate && date <= toDate.AddDays(1);
            }
            catch
            {
                return false;
            }
        }

        private DateTime GetDateValue(string? docDate)
        {
            if (string.IsNullOrEmpty(docDate))
                return DateTime.MinValue;
            if (DateTime.TryParse(docDate, out var parsedDate))
                return parsedDate;
            return DateTime.MinValue;
        }
    }
}
