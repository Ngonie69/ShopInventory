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
        Task<byte[]> GenerateCustomerStatementAsync(CustomerStatementResponseDto statement);
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

        private static string GetCurrencySymbol(string? currency)
        {
            return currency?.ToUpperInvariant() switch
            {
                "USD" => "$",
                "ZIG" => "ZiG",
                "EUR" => "€",
                "GBP" => "£",
                "ZAR" => "R",
                null or "" or "##" => "$",
                var c when c.All(char.IsLetter) => c,
                _ => "$"
            };
        }

        private void InitializeFonts()
        {
            _boldFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
            _regularFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        }

        public async Task<byte[]> GenerateCustomerStatementAsync(CustomerStatementResponseDto statement)
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
                AddStatementBanner(document, statement.FromDate, statement.ToDate);

                // Add customer and statement info side by side
                AddCustomerAndStatementInfo(document, statement.Customer);

                // Add account summary section
                AddAccountSummary(document, statement);

                // Add aging analysis from the precomputed statement summary.
                AddAgingAnalysis(document, statement.Aging);

                // Add transaction ledger from SAP-backed statement rows.
                AddTransactionLedger(document, statement);

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

        private void AddCustomerAndStatementInfo(Document document, StatementCustomerDto customer)
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

            var paymentTerms = string.IsNullOrWhiteSpace(customer.PaymentTermsName)
                ? "Not specified"
                : customer.PaymentTermsDays.HasValue && customer.PaymentTermsDays.Value > 0
                    ? $"{customer.PaymentTermsName} ({customer.PaymentTermsDays} days)"
                    : customer.PaymentTermsName;
            customerCell.Add(new Paragraph($"Payment Terms: {paymentTerms}")
                .SetFont(_regularFont)
                .SetFontSize(9)
                .SetFontColor(TextMuted)
                .SetMarginBottom(2));

            customerCell.Add(new Paragraph($"Currency: {GetCurrencySymbol(customer.Currency)}")
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

            var balance = customer.Balance;
            var balanceColor = balance > 0 ? DangerColor : balance < 0 ? SuccessColor : TextDark;

            balanceCell.Add(new Paragraph($"{GetCurrencySymbol(customer.Currency)} {balance:N2}")
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

        private void AddAccountSummary(Document document, CustomerStatementResponseDto statement)
        {
            var sectionTitle = new Paragraph("ACCOUNT SUMMARY")
                .SetFont(_boldFont)
                .SetFontSize(11)
                .SetFontColor(PrimaryColor)
                .SetMarginBottom(10);
            document.Add(sectionTitle);

            var summaryTable = new Table(new float[] { 1, 1, 1, 1 })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginBottom(15);

            // Summary Cards
            AddSummaryCard(summaryTable, "Opening Balance", $"{statement.OpeningBalance:N2}", $"As at {statement.FromDate:dd MMM yyyy}", PrimaryColor);
            AddSummaryCard(summaryTable, "Total Debits", $"{statement.TotalDebits:N2}", $"{statement.Lines.Count(line => line.Debit > 0)} debit row(s)", DangerColor);
            AddSummaryCard(summaryTable, "Total Credits", $"{statement.TotalCredits:N2}", $"{statement.Lines.Count(line => line.Credit > 0)} credit row(s)", SuccessColor);
            AddSummaryCard(summaryTable, "Closing Balance", $"{statement.ClosingBalance:N2}", $"As at {statement.ToDate:dd MMM yyyy}", statement.ClosingBalance > 0 ? DangerColor : SuccessColor);

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

        private void AddAgingAnalysis(Document document, StatementAgingSummaryDto aging)
        {
            var sectionTitle = new Paragraph("AGING ANALYSIS")
                .SetFont(_boldFont)
                .SetFontSize(11)
                .SetFontColor(PrimaryColor)
                .SetMarginBottom(10);
            document.Add(sectionTitle);

            var agingTable = new Table(new float[] { 1, 1, 1, 1, 1, 1 })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginBottom(15);

            // Headers
            AddAgingHeader(agingTable, "Current", SuccessColor);
            AddAgingHeader(agingTable, aging.Bucket1Label, new DeviceRgb(23, 162, 184));
            AddAgingHeader(agingTable, aging.Bucket2Label, WarningColor);
            AddAgingHeader(agingTable, aging.Bucket3Label, new DeviceRgb(253, 126, 20));
            AddAgingHeader(agingTable, aging.Bucket4Label, DangerColor);
            AddAgingHeader(agingTable, "Total Due", PrimaryColor);

            // Values
            AddAgingValue(agingTable, aging.Current);
            AddAgingValue(agingTable, aging.Days1To30);
            AddAgingValue(agingTable, aging.Days31To60);
            AddAgingValue(agingTable, aging.Days61To90);
            AddAgingValue(agingTable, aging.Over90Days);
            AddAgingValue(agingTable, aging.Total, true);

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

        private void AddTransactionLedger(Document document, CustomerStatementResponseDto statement)
        {
            var sectionTitle = new Paragraph("TRANSACTION DETAILS")
                .SetFont(_boldFont)
                .SetFontSize(11)
                .SetFontColor(PrimaryColor)
                .SetMarginTop(10)
                .SetMarginBottom(10);
            document.Add(sectionTitle);

            // Create the ledger table
            var ledgerTable = new Table(new float[] { 1.1f, 0.9f, 1.2f, 1.2f, 2.1f, 1.1f, 1.1f, 1.2f })
                .SetWidth(UnitValue.CreatePercentValue(100))
                .SetMarginBottom(15);

            // Headers
            AddModernHeader(ledgerTable, "Date");
            AddModernHeader(ledgerTable, "Origin");
            AddModernHeader(ledgerTable, "Origin No.");
            AddModernHeader(ledgerTable, "Offset Acct");
            AddModernHeader(ledgerTable, "Details");
            AddModernHeader(ledgerTable, "Debit");
            AddModernHeader(ledgerTable, "Credit");
            AddModernHeader(ledgerTable, "Balance");

            // Opening balance row
            AddLedgerCell(ledgerTable, statement.FromDate.ToString("dd MMM yyyy"), false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "Opening Balance", false, TextAlignment.LEFT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.RIGHT, null, true);
            AddLedgerCell(ledgerTable, "", false, TextAlignment.RIGHT, null, true);
            AddLedgerCell(ledgerTable, $"{statement.OpeningBalance:N2}", false, TextAlignment.RIGHT, statement.OpeningBalance > 0 ? DangerColor : SuccessColor, true);

            // Transaction rows
            var rowIndex = 0;
            foreach (var line in statement.Lines)
            {
                var isAlternate = rowIndex % 2 == 1;

                AddLedgerCell(ledgerTable, line.Date.ToString("dd MMM yyyy"), isAlternate);
                AddLedgerCell(ledgerTable, line.OriginCode, isAlternate, TextAlignment.LEFT, line.Debit > 0 ? DangerColor : SuccessColor);
                AddLedgerCell(ledgerTable, TruncateText(line.OriginNumber ?? line.DocumentNumber, 16), isAlternate);
                AddLedgerCell(ledgerTable, TruncateText(line.OffsetAccount ?? "-", 16), isAlternate);
                AddLedgerCell(ledgerTable, TruncateText(line.Description ?? line.DocumentType, 38), isAlternate);
                AddLedgerCell(ledgerTable, line.Debit > 0 ? $"{line.Debit:N2}" : "-", isAlternate, TextAlignment.RIGHT, line.Debit > 0 ? DangerColor : TextMuted);
                AddLedgerCell(ledgerTable, line.Credit > 0 ? $"{line.Credit:N2}" : "-", isAlternate, TextAlignment.RIGHT, line.Credit > 0 ? SuccessColor : TextMuted);
                AddLedgerCell(ledgerTable, $"{line.Balance:N2}", isAlternate, TextAlignment.RIGHT, line.Balance > 0 ? DangerColor : SuccessColor);

                rowIndex++;
            }

            AddLedgerTotalCell(ledgerTable, "");
            AddLedgerTotalCell(ledgerTable, "");
            AddLedgerTotalCell(ledgerTable, "");
            AddLedgerTotalCell(ledgerTable, "");
            AddLedgerTotalCell(ledgerTable, "TOTALS");
            AddLedgerTotalCell(ledgerTable, $"{statement.TotalDebits:N2}", DangerColor);
            AddLedgerTotalCell(ledgerTable, $"{statement.TotalCredits:N2}", SuccessColor);
            AddLedgerTotalCell(ledgerTable, $"{statement.ClosingBalance:N2}", statement.ClosingBalance > 0 ? DangerColor : SuccessColor);

            document.Add(ledgerTable);

            // Closing balance note
            var closingNote = new Paragraph($"Closing Balance: {GetCurrencySymbol(statement.Customer.Currency)} {statement.ClosingBalance:N2}")
                .SetFont(_boldFont)
                .SetFontSize(10)
                .SetFontColor(statement.ClosingBalance > 0 ? DangerColor : SuccessColor)
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
