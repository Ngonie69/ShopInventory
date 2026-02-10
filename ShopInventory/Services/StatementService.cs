using iText.Kernel.Pdf;
using iText.Kernel.Font;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using ShopInventory.DTOs;

namespace ShopInventory.Services
{
    public interface IStatementService
    {
        Task<byte[]> GenerateCustomerStatementAsync(
            BusinessPartnerDto customer,
            IEnumerable<InvoiceDto> invoices,
            IEnumerable<IncomingPaymentDto> payments,
            DateTime fromDate,
            DateTime toDate,
            IEnumerable<InvoiceDto>? allOpenInvoices = null);
    }

    public class StatementService : IStatementService
    {
        private readonly ILogger<StatementService> _logger;
        private PdfFont? _boldFont;
        private PdfFont? _regularFont;

        // Professional color scheme
        private static readonly Color PrimaryColor = new DeviceRgb(0, 51, 102);      // Deep navy blue
        private static readonly Color SecondaryColor = new DeviceRgb(0, 102, 153);   // Medium blue
        private static readonly Color AccentColor = new DeviceRgb(218, 165, 32);     // Gold/Yellow
        private static readonly Color HeaderBgColor = new DeviceRgb(240, 248, 255);  // Alice blue
        private static readonly Color AlternateRowColor = new DeviceRgb(248, 250, 252); // Light gray-blue
        private static readonly Color TextDark = new DeviceRgb(33, 37, 41);          // Dark gray
        private static readonly Color TextMuted = new DeviceRgb(108, 117, 125);      // Muted gray
        private static readonly Color SuccessColor = new DeviceRgb(40, 167, 69);     // Green
        private static readonly Color DangerColor = new DeviceRgb(220, 53, 69);      // Red
        private static readonly Color WarningColor = new DeviceRgb(255, 193, 7);     // Yellow

        public StatementService(ILogger<StatementService> logger)
        {
            _logger = logger;
        }

        private void InitializeFonts()
        {
            _boldFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
            _regularFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        }

        public async Task<byte[]> GenerateCustomerStatementAsync(
            BusinessPartnerDto customer,
            IEnumerable<InvoiceDto> invoices,
            IEnumerable<IncomingPaymentDto> payments,
            DateTime fromDate,
            DateTime toDate,
            IEnumerable<InvoiceDto>? allOpenInvoices = null)
        {
            try
            {
                InitializeFonts();

                using var memoryStream = new MemoryStream();
                var writerProperties = new WriterProperties();
                var writer = new PdfWriter(memoryStream, writerProperties);
                writer.SetCloseStream(false);
                var pdf = new PdfDocument(writer);
                var document = new Document(pdf, PageSize.A4);

                // Set document margins
                document.SetMargins(30, 30, 50, 30);

                // Add company header with logo
                AddCompanyHeader(document);

                // Add statement title banner
                AddStatementBanner(document, fromDate, toDate);

                // Add customer and statement info side by side
                AddCustomerAndStatementInfo(document, customer);

                // Add account summary section
                AddAccountSummary(document, customer, invoices, payments, fromDate, toDate);

                // Add aging analysis (use all open invoices if provided, otherwise fall back to filtered invoices)
                AddAgingAnalysis(document, allOpenInvoices ?? invoices);

                // Add transaction ledger (chronological debit/credit with running balance)
                AddTransactionLedger(document, customer, invoices, payments, fromDate);

                // Add footer with page numbers
                AddFooter(document, pdf);

                document.Close();

                return await Task.FromResult(memoryStream.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating PDF statement: {Message}", ex.Message);
                throw;
            }
        }

        private void AddCompanyHeader(Document document)
        {
            // Create header table with logo and company info
            var headerTable = new Table(new float[] { 1, 3 });
            headerTable.SetWidth(UnitValue.CreatePercentValue(100));
            headerTable.SetMarginBottom(15);

            // Logo cell - Using embedded logo
            var logoCell = new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                .SetPaddingRight(15);

            try
            {
                // Try to load logo from file or use embedded base64
                var logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "kefalos-logo.jpg");
                if (File.Exists(logoPath))
                {
                    var logoImage = new Image(ImageDataFactory.Create(logoPath))
                        .SetWidth(100)
                        .SetAutoScale(true);
                    logoCell.Add(logoImage);
                }
                else
                {
                    // Fallback: Create a styled text logo
                    var logoDiv = new Div()
                        .SetWidth(80)
                        .SetHeight(80)
                        .SetBackgroundColor(PrimaryColor)
                        .SetBorderRadius(new BorderRadius(8))
                        .SetTextAlignment(TextAlignment.CENTER)
                        .SetVerticalAlignment(VerticalAlignment.MIDDLE);

                    var logoText = new Paragraph("K")
                        .SetFont(_boldFont)
                        .SetFontSize(48)
                        .SetFontColor(ColorConstants.WHITE)
                        .SetTextAlignment(TextAlignment.CENTER)
                        .SetMarginTop(12);
                    logoDiv.Add(logoText);
                    logoCell.Add(logoDiv);
                }
            }
            catch
            {
                // If logo loading fails, add text placeholder
                var logoPlaceholder = new Paragraph("KEFALOS")
                    .SetFont(_boldFont)
                    .SetFontSize(24)
                    .SetFontColor(PrimaryColor);
                logoCell.Add(logoPlaceholder);
            }

            headerTable.AddCell(logoCell);

            // Company info cell
            var infoCell = new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetVerticalAlignment(VerticalAlignment.MIDDLE);

            var companyName = new Paragraph("KEFALOS CHEESE PRODUCTS PVT (LTD)")
                .SetFont(_boldFont)
                .SetFontSize(18)
                .SetFontColor(PrimaryColor)
                .SetMarginBottom(2);
            infoCell.Add(companyName);

            var tagline = new Paragraph("QUALITY DAIRY PRODUCE")
                .SetFont(_regularFont)
                .SetFontSize(9)
                .SetFontColor(AccentColor)
                .SetMarginBottom(8);
            infoCell.Add(tagline);

            var addressDiv = new Div()
                .SetFontSize(8)
                .SetFontColor(TextMuted);

            addressDiv.Add(new Paragraph("Head Office: 35C Kingsmead Road, Borrowdale, Harare, Zimbabwe")
                .SetMarginBottom(1));
            addressDiv.Add(new Paragraph("Factory: Bhara Bhara Farm, Mubaira Road, Harare South")
                .SetMarginBottom(1));
            addressDiv.Add(new Paragraph("Tel: +263 242 764 301/02/03 | Email: marketing@kefaloscheese.com")
                .SetMarginBottom(1));
            addressDiv.Add(new Paragraph("Website: www.kefalosfood.com"));

            infoCell.Add(addressDiv);
            headerTable.AddCell(infoCell);

            document.Add(headerTable);

            // Add decorative line
            var lineTable = new Table(1)
                .SetWidth(UnitValue.CreatePercentValue(100));
            var lineCell = new Cell()
                .SetHeight(3)
                .SetBackgroundColor(AccentColor)
                .SetBorder(Border.NO_BORDER);
            lineTable.AddCell(lineCell);
            document.Add(lineTable);
        }

        private void AddStatementBanner(Document document, DateTime fromDate, DateTime toDate)
        {
            var bannerTable = new Table(1)
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginTop(15)
                .SetMarginBottom(15);

            var bannerCell = new Cell()
                .SetBackgroundColor(PrimaryColor)
                .SetPadding(12)
                .SetBorder(Border.NO_BORDER)
                .SetBorderRadius(new BorderRadius(5));

            var bannerContent = new Table(2)
                .SetWidth(UnitValue.CreatePercentValue(100));

            var titleCell = new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetVerticalAlignment(VerticalAlignment.MIDDLE);
            titleCell.Add(new Paragraph("STATEMENT OF ACCOUNT")
                .SetFont(_boldFont)
                .SetFontSize(16)
                .SetFontColor(ColorConstants.WHITE));
            bannerContent.AddCell(titleCell);

            var dateCell = new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetTextAlignment(TextAlignment.RIGHT)
                .SetVerticalAlignment(VerticalAlignment.MIDDLE);
            dateCell.Add(new Paragraph($"Period: {fromDate:dd MMM yyyy} - {toDate:dd MMM yyyy}")
                .SetFont(_regularFont)
                .SetFontSize(10)
                .SetFontColor(ColorConstants.WHITE));
            dateCell.Add(new Paragraph($"Generated: {DateTime.Now:dd MMM yyyy HH:mm}")
                .SetFont(_regularFont)
                .SetFontSize(9)
                .SetFontColor(new DeviceRgb(200, 200, 200)));
            bannerContent.AddCell(dateCell);

            bannerCell.Add(bannerContent);
            bannerTable.AddCell(bannerCell);
            document.Add(bannerTable);
        }

        private void AddCustomerAndStatementInfo(Document document, BusinessPartnerDto customer)
        {
            var infoTable = new Table(new float[] { 1, 1 })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginBottom(15);

            // Customer Details Box
            var customerCell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(222, 226, 230), 1))
                .SetBorderRadius(new BorderRadius(5))
                .SetPadding(12)
                .SetPaddingRight(20);

            customerCell.Add(new Paragraph("BILL TO")
                .SetFont(_boldFont)
                .SetFontSize(9)
                .SetFontColor(TextMuted)
                .SetMarginBottom(8));

            customerCell.Add(new Paragraph(customer.CardName ?? "-")
                .SetFont(_boldFont)
                .SetFontSize(12)
                .SetFontColor(TextDark)
                .SetMarginBottom(4));

            customerCell.Add(new Paragraph($"Account No: {customer.CardCode ?? "-"}")
                .SetFont(_regularFont)
                .SetFontSize(9)
                .SetFontColor(TextMuted)
                .SetMarginBottom(2));

            var type = customer.CardType == "C" ? "Customer" : customer.CardType == "S" ? "Supplier" : "Lead";
            customerCell.Add(new Paragraph($"Type: {type}")
                .SetFont(_regularFont)
                .SetFontSize(9)
                .SetFontColor(TextMuted)
                .SetMarginBottom(2));

            customerCell.Add(new Paragraph($"Currency: {customer.Currency ?? "USD"}")
                .SetFont(_regularFont)
                .SetFontSize(9)
                .SetFontColor(TextMuted));

            infoTable.AddCell(customerCell);

            // Balance Summary Box
            var balanceCell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(222, 226, 230), 1))
                .SetBorderRadius(new BorderRadius(5))
                .SetPadding(12)
                .SetBackgroundColor(HeaderBgColor);

            balanceCell.Add(new Paragraph("ACCOUNT BALANCE")
                .SetFont(_boldFont)
                .SetFontSize(9)
                .SetFontColor(TextMuted)
                .SetMarginBottom(8));

            var balance = customer.Balance ?? 0;
            var balanceColor = balance > 0 ? DangerColor : balance < 0 ? SuccessColor : TextDark;

            balanceCell.Add(new Paragraph($"{customer.Currency ?? "USD"} {balance:N2}")
                .SetFont(_boldFont)
                .SetFontSize(24)
                .SetFontColor(balanceColor)
                .SetMarginBottom(4));

            var balanceStatus = balance > 0 ? "Amount Due" : balance < 0 ? "Credit Balance" : "Settled";
            balanceCell.Add(new Paragraph(balanceStatus)
                .SetFont(_regularFont)
                .SetFontSize(10)
                .SetFontColor(balanceColor));

            infoTable.AddCell(balanceCell);
            document.Add(infoTable);
        }

        private void AddAccountSummary(Document document, BusinessPartnerDto customer, IEnumerable<InvoiceDto> invoices, IEnumerable<IncomingPaymentDto> payments, DateTime fromDate, DateTime toDate)
        {
            var sectionTitle = new Paragraph("ACCOUNT SUMMARY")
                .SetFont(_boldFont)
                .SetFontSize(11)
                .SetFontColor(PrimaryColor)
                .SetMarginBottom(10);
            document.Add(sectionTitle);

            var totalInvoiced = invoices?.Sum(i => i.DocTotal) ?? 0;
            var totalPaid = payments?.Sum(p => p.DocTotal) ?? 0;

            // Calculate opening balance: current SAP balance - period invoices + period payments
            var currentBalance = customer.Balance ?? 0;
            var openingBalance = currentBalance - totalInvoiced + totalPaid;
            var closingBalance = currentBalance;

            var summaryTable = new Table(new float[] { 1, 1, 1, 1 })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginBottom(15);

            // Summary Cards
            AddSummaryCard(summaryTable, "Opening Balance", $"{openingBalance:N2}", $"As at {fromDate:dd MMM yyyy}", PrimaryColor);
            AddSummaryCard(summaryTable, "Total Debits", $"{totalInvoiced:N2}", $"{invoices?.Count() ?? 0} Invoice(s)", DangerColor);
            AddSummaryCard(summaryTable, "Total Credits", $"{totalPaid:N2}", $"{payments?.Count() ?? 0} Payment(s)", SuccessColor);
            AddSummaryCard(summaryTable, "Closing Balance", $"{closingBalance:N2}", $"As at {toDate:dd MMM yyyy}", closingBalance > 0 ? DangerColor : SuccessColor);

            document.Add(summaryTable);
        }

        private void AddSummaryCard(Table table, string label, string value, string subtext, Color accentColor)
        {
            var cell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(222, 226, 230), 1))
                .SetBorderRadius(new BorderRadius(5))
                .SetPadding(10)
                .SetTextAlignment(TextAlignment.CENTER);

            // Top accent bar
            var accentBar = new Div()
                .SetHeight(3)
                .SetBackgroundColor(accentColor)
                .SetMarginBottom(8);
            cell.Add(accentBar);

            cell.Add(new Paragraph(label)
                .SetFont(_regularFont)
                .SetFontSize(8)
                .SetFontColor(TextMuted)
                .SetMarginBottom(4));

            cell.Add(new Paragraph(value)
                .SetFont(_boldFont)
                .SetFontSize(16)
                .SetFontColor(TextDark)
                .SetMarginBottom(2));

            cell.Add(new Paragraph(subtext)
                .SetFont(_regularFont)
                .SetFontSize(8)
                .SetFontColor(TextMuted));

            table.AddCell(cell);
        }

        private void AddAgingAnalysis(Document document, IEnumerable<InvoiceDto> invoices)
        {
            var sectionTitle = new Paragraph("AGING ANALYSIS")
                .SetFont(_boldFont)
                .SetFontSize(11)
                .SetFontColor(PrimaryColor)
                .SetMarginBottom(10);
            document.Add(sectionTitle);

            // Calculate aging buckets
            var today = DateTime.Today;
            var openInvoices = invoices?.Where(i => i.DocStatus == "O").ToList() ?? new List<InvoiceDto>();

            decimal current = 0, days30 = 0, days60 = 0, days90 = 0, over90 = 0;

            foreach (var inv in openInvoices)
            {
                if (!DateTime.TryParse(inv.DocDueDate, out var dueDate))
                    continue;

                var daysOverdue = (today - dueDate).Days;
                var balance = inv.DocTotal - inv.PaidToDate;

                if (balance <= 0)
                    continue;

                if (daysOverdue <= 0)
                    current += balance;
                else if (daysOverdue <= 30)
                    days30 += balance;
                else if (daysOverdue <= 60)
                    days60 += balance;
                else if (daysOverdue <= 90)
                    days90 += balance;
                else
                    over90 += balance;
            }

            var total = current + days30 + days60 + days90 + over90;

            var agingTable = new Table(new float[] { 1, 1, 1, 1, 1, 1 })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginBottom(15);

            // Headers
            AddAgingHeader(agingTable, "Current", SuccessColor);
            AddAgingHeader(agingTable, "1-30 Days", new DeviceRgb(23, 162, 184));
            AddAgingHeader(agingTable, "31-60 Days", WarningColor);
            AddAgingHeader(agingTable, "61-90 Days", new DeviceRgb(253, 126, 20));
            AddAgingHeader(agingTable, "90+ Days", DangerColor);
            AddAgingHeader(agingTable, "Total Due", PrimaryColor);

            // Values
            AddAgingValue(agingTable, current);
            AddAgingValue(agingTable, days30);
            AddAgingValue(agingTable, days60);
            AddAgingValue(agingTable, days90);
            AddAgingValue(agingTable, over90);
            AddAgingValue(agingTable, total, true);

            document.Add(agingTable);
        }

        private void AddAgingHeader(Table table, string text, Color bgColor)
        {
            var cell = new Cell()
                .SetBackgroundColor(bgColor)
                .SetBorder(Border.NO_BORDER)
                .SetPadding(8)
                .SetTextAlignment(TextAlignment.CENTER);
            cell.Add(new Paragraph(text)
                .SetFont(_boldFont)
                .SetFontSize(9)
                .SetFontColor(ColorConstants.WHITE));
            table.AddHeaderCell(cell);
        }

        private void AddAgingValue(Table table, decimal value, bool isTotal = false)
        {
            var cell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(222, 226, 230), 1))
                .SetPadding(8)
                .SetTextAlignment(TextAlignment.CENTER);
            if (isTotal)
                cell.SetBackgroundColor(HeaderBgColor);

            cell.Add(new Paragraph($"{value:N2}")
                .SetFont(isTotal ? _boldFont : _regularFont)
                .SetFontSize(10)
                .SetFontColor(value > 0 ? (isTotal ? PrimaryColor : TextDark) : TextMuted));
            table.AddCell(cell);
        }

        private void AddTransactionLedger(Document document, BusinessPartnerDto customer, IEnumerable<InvoiceDto> invoices, IEnumerable<IncomingPaymentDto> payments, DateTime fromDate)
        {
            var sectionTitle = new Paragraph("TRANSACTION DETAILS")
                .SetFont(_boldFont)
                .SetFontSize(11)
                .SetFontColor(PrimaryColor)
                .SetMarginTop(10)
                .SetMarginBottom(10);
            document.Add(sectionTitle);

            // Build a combined chronological list of transactions
            var transactions = new List<(DateTime Date, string Type, string Reference, string Description, decimal Debit, decimal Credit)>();

            if (invoices != null)
            {
                foreach (var inv in invoices)
                {
                    var date = DateTime.TryParse(inv.DocDate, out var d) ? d : DateTime.MinValue;
                    var description = !string.IsNullOrEmpty(inv.Remarks) ? inv.Remarks
                        : !string.IsNullOrEmpty(inv.Comments) ? inv.Comments
                        : "Invoice";
                    transactions.Add((date, "Invoice", $"INV-{inv.DocNum}", TruncateText(description, 35), inv.DocTotal, 0));
                }
            }

            if (payments != null)
            {
                foreach (var pmt in payments)
                {
                    var date = DateTime.TryParse(pmt.DocDate, out var d) ? d : DateTime.MinValue;
                    var type = DeterminePaymentType(pmt);
                    var reference = !string.IsNullOrEmpty(pmt.TransferReference) ? pmt.TransferReference : $"RCT-{pmt.DocNum}";
                    var description = !string.IsNullOrEmpty(pmt.Remarks) ? pmt.Remarks : $"Payment - {type}";
                    transactions.Add((date, "Payment", TruncateText(reference, 18), TruncateText(description, 35), 0, pmt.DocTotal));
                }
            }

            // Sort chronologically
            transactions = transactions.OrderBy(t => t.Date).ThenBy(t => t.Type == "Invoice" ? 0 : 1).ToList();

            // Create the ledger table
            var ledgerTable = new Table(new float[] { 1.1f, 1.2f, 1.4f, 2.2f, 1.2f, 1.2f, 1.2f })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginBottom(15);

            // Headers
            AddModernHeader(ledgerTable, "Date");
            AddModernHeader(ledgerTable, "Type");
            AddModernHeader(ledgerTable, "Reference");
            AddModernHeader(ledgerTable, "Description");
            AddModernHeader(ledgerTable, "Debit");
            AddModernHeader(ledgerTable, "Credit");
            AddModernHeader(ledgerTable, "Balance");

            // Calculate opening balance
            var totalInvoiced = invoices?.Sum(i => i.DocTotal) ?? 0;
            var totalPaid = payments?.Sum(p => p.DocTotal) ?? 0;
            var currentBalance = customer.Balance ?? 0;
            var openingBalance = currentBalance - totalInvoiced + totalPaid;
            var runningBalance = openingBalance;

            // Opening balance row
            AddLedgerCell(ledgerTable, fromDate.ToString("dd MMM yyyy"), false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "Opening Balance", false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.RIGHT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.RIGHT, null, true);
            AddLedgerCell(ledgerTable, $"{openingBalance:N2}", false, TextAlignment.RIGHT, openingBalance > 0 ? DangerColor : SuccessColor, true);

            // Transaction rows
            var rowIndex = 0;
            foreach (var txn in transactions)
            {
                var isAlternate = rowIndex % 2 == 1;

                // Update running balance: debits increase, credits decrease
                runningBalance += txn.Debit - txn.Credit;

                AddLedgerCell(ledgerTable, txn.Date.ToString("dd MMM yyyy"), isAlternate);
                AddLedgerCell(ledgerTable, txn.Type, isAlternate, TextAlignment.LEFT, txn.Type == "Invoice" ? DangerColor : SuccessColor);
                AddLedgerCell(ledgerTable, txn.Reference, isAlternate);
                AddLedgerCell(ledgerTable, txn.Description, isAlternate);
                AddLedgerCell(ledgerTable, txn.Debit > 0 ? $"{txn.Debit:N2}" : "-", isAlternate, TextAlignment.RIGHT, txn.Debit > 0 ? DangerColor : TextMuted);
                AddLedgerCell(ledgerTable, txn.Credit > 0 ? $"{txn.Credit:N2}" : "-", isAlternate, TextAlignment.RIGHT, txn.Credit > 0 ? SuccessColor : TextMuted);
                AddLedgerCell(ledgerTable, $"{runningBalance:N2}", isAlternate, TextAlignment.RIGHT, runningBalance > 0 ? DangerColor : SuccessColor);

                rowIndex++;
            }

            // Totals row
            var totalDebit = transactions.Sum(t => t.Debit);
            var totalCredit = transactions.Sum(t => t.Credit);

            AddLedgerTotalCell(ledgerTable, "");
            AddLedgerTotalCell(ledgerTable, "");
            AddLedgerTotalCell(ledgerTable, "");
            AddLedgerTotalCell(ledgerTable, "TOTALS");
            AddLedgerTotalCell(ledgerTable, $"{totalDebit:N2}", DangerColor);
            AddLedgerTotalCell(ledgerTable, $"{totalCredit:N2}", SuccessColor);
            AddLedgerTotalCell(ledgerTable, $"{runningBalance:N2}", runningBalance > 0 ? DangerColor : SuccessColor);

            document.Add(ledgerTable);

            // Closing balance note
            var closingNote = new Paragraph($"Closing Balance: {customer.Currency ?? "USD"} {runningBalance:N2}")
                .SetFont(_boldFont)
                .SetFontSize(10)
                .SetFontColor(runningBalance > 0 ? DangerColor : SuccessColor)
                .SetTextAlignment(TextAlignment.RIGHT)
                .SetMarginBottom(10);
            document.Add(closingNote);
        }

        private void AddLedgerCell(Table table, string text, bool isAlternate, TextAlignment alignment = TextAlignment.LEFT, Color? textColor = null, bool isBold = false)
        {
            var cell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(222, 226, 230), 0.5f))
                .SetPadding(5);

            if (isAlternate)
                cell.SetBackgroundColor(AlternateRowColor);

            var para = new Paragraph(text)
                .SetFont(isBold ? _boldFont : _regularFont)
                .SetFontSize(8)
                .SetFontColor(textColor ?? TextDark)
                .SetTextAlignment(alignment);

            cell.Add(para);
            table.AddCell(cell);
        }

        private void AddLedgerTotalCell(Table table, string text, Color? textColor = null)
        {
            var cell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(222, 226, 230), 0.5f))
                .SetBorderTop(new SolidBorder(PrimaryColor, 1.5f))
                .SetPadding(6)
                .SetBackgroundColor(HeaderBgColor);

            var para = new Paragraph(text)
                .SetFont(_boldFont)
                .SetFontSize(9)
                .SetFontColor(textColor ?? PrimaryColor)
                .SetTextAlignment(string.IsNullOrEmpty(text) ? TextAlignment.LEFT : TextAlignment.RIGHT);

            cell.Add(para);
            table.AddCell(cell);
        }

        private void AddModernHeader(Table table, string text)
        {
            var cell = new Cell()
                .SetBackgroundColor(PrimaryColor)
                .SetBorder(Border.NO_BORDER)
                .SetPadding(8);
            cell.Add(new Paragraph(text)
                .SetFont(_boldFont)
                .SetFontSize(9)
                .SetFontColor(ColorConstants.WHITE));
            table.AddHeaderCell(cell);
        }

        private void AddDataCell(Table table, string text, bool isAlternate, TextAlignment? alignment = null, Color? textColor = null)
        {
            var cell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(238, 238, 238), 0.5f))
                .SetPadding(6);

            if (isAlternate)
                cell.SetBackgroundColor(AlternateRowColor);

            var para = new Paragraph(text)
                .SetFont(_regularFont)
                .SetFontSize(9)
                .SetFontColor(textColor ?? TextDark);

            if (alignment.HasValue)
                para.SetTextAlignment(alignment.Value);

            cell.Add(para);
            table.AddCell(cell);
        }

        private void AddStatusCell(Table table, string status, Color statusColor, bool isAlternate)
        {
            var cell = new Cell()
                .SetBorder(new SolidBorder(new DeviceRgb(238, 238, 238), 0.5f))
                .SetPadding(6)
                .SetTextAlignment(TextAlignment.CENTER);

            if (isAlternate)
                cell.SetBackgroundColor(AlternateRowColor);

            var statusPara = new Paragraph(status)
                .SetFont(_boldFont)
                .SetFontSize(8)
                .SetFontColor(statusColor);

            cell.Add(statusPara);
            table.AddCell(cell);
        }

        private string DeterminePaymentType(IncomingPaymentDto payment)
        {
            try
            {
                if (payment.CashSum > 0)
                    return "Cash";
                else if (payment.CheckSum > 0)
                    return "Cheque";
                else if (payment.TransferSum > 0)
                    return "Bank Transfer";
                else if (payment.CreditSum > 0)
                    return "Credit";
                else
                    return "Receipt";
            }
            catch
            {
                return "Receipt";
            }
        }

        private void AddFooter(Document document, PdfDocument pdf)
        {
            // Add spacing before footer
            document.Add(new Paragraph("\n"));

            // Footer separator
            var lineTable = new Table(1)
                .SetWidth(UnitValue.CreatePercentValue(100));
            var lineCell = new Cell()
                .SetHeight(1)
                .SetBackgroundColor(new DeviceRgb(222, 226, 230))
                .SetBorder(Border.NO_BORDER);
            lineTable.AddCell(lineCell);
            document.Add(lineTable);

            // Footer content
            var footerTable = new Table(new float[] { 2, 1 })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginTop(10);

            var leftCell = new Cell()
                .SetBorder(Border.NO_BORDER);
            leftCell.Add(new Paragraph("Terms & Conditions")
                .SetFont(_boldFont)
                .SetFontSize(8)
                .SetFontColor(TextDark)
                .SetMarginBottom(3));
            leftCell.Add(new Paragraph("• Payment is due within the terms stated on individual invoices")
                .SetFont(_regularFont)
                .SetFontSize(7)
                .SetFontColor(TextMuted)
                .SetMarginBottom(1));
            leftCell.Add(new Paragraph("• Please quote invoice numbers when making payments")
                .SetFont(_regularFont)
                .SetFontSize(7)
                .SetFontColor(TextMuted)
                .SetMarginBottom(1));
            leftCell.Add(new Paragraph("• For queries, contact: marketing@kefaloscheese.com | +263 242 764 301")
                .SetFont(_regularFont)
                .SetFontSize(7)
                .SetFontColor(TextMuted));
            footerTable.AddCell(leftCell);

            var rightCell = new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetTextAlignment(TextAlignment.RIGHT)
                .SetVerticalAlignment(VerticalAlignment.BOTTOM);
            rightCell.Add(new Paragraph("Thank you for your business!")
                .SetFont(_boldFont)
                .SetFontSize(9)
                .SetFontColor(PrimaryColor)
                .SetMarginBottom(5));
            rightCell.Add(new Paragraph("This is a computer-generated statement.")
                .SetFont(_regularFont)
                .SetFontSize(7)
                .SetFontColor(TextMuted));
            footerTable.AddCell(rightCell);

            document.Add(footerTable);
        }

        private string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Formats a date string to a customer-friendly format (e.g., "11 Jan 2026")
        /// </summary>
        private string FormatDate(string? dateString)
        {
            if (string.IsNullOrEmpty(dateString))
                return "-";

            // Try parsing various date formats
            if (DateTime.TryParse(dateString, out var date))
            {
                return date.ToString("dd MMM yyyy");
            }

            // If parsing fails, return original or dash
            return dateString;
        }
    }
}
