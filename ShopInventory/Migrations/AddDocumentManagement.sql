-- Document Management Module Migration
-- Run this script after: dotnet ef migrations add AddDocumentManagement

-- Create DocumentTemplates table
CREATE TABLE IF NOT EXISTS "DocumentTemplates" (
    "Id" SERIAL PRIMARY KEY,
    "Name" VARCHAR(100) NOT NULL,
    "DocumentType" VARCHAR(50) NOT NULL,
    "HtmlContent" TEXT NOT NULL,
    "CssStyles" TEXT,
    "HeaderContent" TEXT,
    "FooterContent" TEXT,
    "PaperSize" VARCHAR(20) NOT NULL DEFAULT 'A4',
    "Orientation" VARCHAR(20) NOT NULL DEFAULT 'Portrait',
    "IsDefault" BOOLEAN NOT NULL DEFAULT FALSE,
    "IsActive" BOOLEAN NOT NULL DEFAULT TRUE,
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" TIMESTAMP,
    "CreatedByUserId" UUID,
    CONSTRAINT "FK_DocumentTemplates_Users_CreatedByUserId" 
        FOREIGN KEY ("CreatedByUserId") REFERENCES "Users"("Id") ON DELETE SET NULL
);

CREATE INDEX "IX_DocumentTemplates_DocumentType" ON "DocumentTemplates"("DocumentType");
CREATE INDEX "IX_DocumentTemplates_IsDefault" ON "DocumentTemplates"("IsDefault");
CREATE INDEX "IX_DocumentTemplates_IsActive" ON "DocumentTemplates"("IsActive");

-- Create DocumentAttachments table
CREATE TABLE IF NOT EXISTS "DocumentAttachments" (
    "Id" SERIAL PRIMARY KEY,
    "EntityType" VARCHAR(50) NOT NULL,
    "EntityId" INTEGER NOT NULL,
    "FileName" VARCHAR(255) NOT NULL,
    "StoredFileName" VARCHAR(500) NOT NULL,
    "MimeType" VARCHAR(100),
    "FileSizeBytes" BIGINT NOT NULL,
    "Description" VARCHAR(500),
    "IncludeInEmail" BOOLEAN NOT NULL DEFAULT TRUE,
    "UploadedAt" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UploadedByUserId" UUID,
    CONSTRAINT "FK_DocumentAttachments_Users_UploadedByUserId" 
        FOREIGN KEY ("UploadedByUserId") REFERENCES "Users"("Id") ON DELETE SET NULL
);

CREATE INDEX "IX_DocumentAttachments_EntityType_EntityId" 
    ON "DocumentAttachments"("EntityType", "EntityId");
CREATE INDEX "IX_DocumentAttachments_UploadedAt" ON "DocumentAttachments"("UploadedAt");

-- Create DocumentHistory table
CREATE TABLE IF NOT EXISTS "DocumentHistory" (
    "Id" SERIAL PRIMARY KEY,
    "DocumentType" VARCHAR(50) NOT NULL,
    "EntityId" INTEGER NOT NULL,
    "DocumentNumber" VARCHAR(50),
    "TemplateId" INTEGER,
    "FilePath" VARCHAR(500),
    "Action" VARCHAR(50) NOT NULL,
    "RecipientEmail" VARCHAR(255),
    "EmailSubject" VARCHAR(500),
    "EmailSent" BOOLEAN,
    "EmailError" VARCHAR(1000),
    "GeneratedAt" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "GeneratedByUserId" UUID,
    CONSTRAINT "FK_DocumentHistory_DocumentTemplates_TemplateId" 
        FOREIGN KEY ("TemplateId") REFERENCES "DocumentTemplates"("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_DocumentHistory_Users_GeneratedByUserId" 
        FOREIGN KEY ("GeneratedByUserId") REFERENCES "Users"("Id") ON DELETE SET NULL
);

CREATE INDEX "IX_DocumentHistory_DocumentType_EntityId" 
    ON "DocumentHistory"("DocumentType", "EntityId");
CREATE INDEX "IX_DocumentHistory_GeneratedAt" ON "DocumentHistory"("GeneratedAt");
CREATE INDEX "IX_DocumentHistory_Action" ON "DocumentHistory"("Action");

-- Create DocumentSignatures table
CREATE TABLE IF NOT EXISTS "DocumentSignatures" (
    "Id" SERIAL PRIMARY KEY,
    "DocumentType" VARCHAR(50) NOT NULL,
    "DocumentId" INTEGER NOT NULL,
    "SignerName" VARCHAR(200) NOT NULL,
    "SignerEmail" VARCHAR(255),
    "SignerRole" VARCHAR(50) NOT NULL,
    "SignatureData" TEXT NOT NULL,
    "IpAddress" VARCHAR(50),
    "DeviceInfo" VARCHAR(500),
    "SignedAt" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "IsVerified" BOOLEAN NOT NULL DEFAULT TRUE,
    "UserId" UUID,
    CONSTRAINT "FK_DocumentSignatures_Users_UserId" 
        FOREIGN KEY ("UserId") REFERENCES "Users"("Id") ON DELETE SET NULL
);

CREATE INDEX "IX_DocumentSignatures_DocumentType_DocumentId" 
    ON "DocumentSignatures"("DocumentType", "DocumentId");
CREATE INDEX "IX_DocumentSignatures_SignedAt" ON "DocumentSignatures"("SignedAt");

-- Create EmailTemplates table
CREATE TABLE IF NOT EXISTS "EmailTemplates" (
    "Id" SERIAL PRIMARY KEY,
    "Name" VARCHAR(100) NOT NULL,
    "TemplateCode" VARCHAR(50) NOT NULL UNIQUE,
    "Subject" VARCHAR(500) NOT NULL,
    "BodyContent" TEXT NOT NULL,
    "CcEmails" VARCHAR(1000),
    "BccEmails" VARCHAR(1000),
    "IsActive" BOOLEAN NOT NULL DEFAULT TRUE,
    "CreatedAt" TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "UpdatedAt" TIMESTAMP
);

CREATE UNIQUE INDEX "IX_EmailTemplates_TemplateCode" ON "EmailTemplates"("TemplateCode");
CREATE INDEX "IX_EmailTemplates_IsActive" ON "EmailTemplates"("IsActive");

-- Insert default invoice template
INSERT INTO "DocumentTemplates" ("Name", "DocumentType", "HtmlContent", "CssStyles", "HeaderContent", "FooterContent", "IsDefault", "IsActive")
VALUES (
    'Default Invoice Template',
    'Invoice',
    '<div class="invoice-body">
        <div class="invoice-info">
            <h2>Invoice #{{InvoiceNumber}}</h2>
            <p><strong>Date:</strong> {{DocDate}}</p>
            <p><strong>Due Date:</strong> {{DueDate}}</p>
        </div>
        <div class="customer-info">
            <h3>Bill To:</h3>
            <p><strong>{{CustomerName}}</strong></p>
            <p>Customer Code: {{CustomerCode}}</p>
        </div>
        <table class="invoice-table">
            <thead>
                <tr>
                    <th>Description</th>
                    <th>Quantity</th>
                    <th>Unit Price</th>
                    <th>Total</th>
                </tr>
            </thead>
            <tbody>
                <!-- Line items will be inserted here -->
            </tbody>
        </table>
        <div class="invoice-totals">
            <p><strong>Subtotal:</strong> {{Currency}} {{Total}}</p>
            <p><strong>VAT:</strong> {{Currency}} {{VatSum}}</p>
            <h3><strong>Total:</strong> {{Currency}} {{Total}}</h3>
        </div>
    </div>',
    'body { font-family: Arial, sans-serif; }
    .invoice-body { padding: 20px; }
    .invoice-info { margin-bottom: 20px; }
    .customer-info { margin-bottom: 20px; padding: 15px; background: #f5f5f5; }
    .invoice-table { width: 100%; border-collapse: collapse; margin: 20px 0; }
    .invoice-table th, .invoice-table td { border: 1px solid #ddd; padding: 12px; text-align: left; }
    .invoice-table th { background: #4CAF50; color: white; }
    .invoice-totals { text-align: right; margin-top: 20px; padding: 15px; background: #f9f9f9; }',
    '<div class="invoice-header" style="text-align: center; border-bottom: 2px solid #4CAF50; padding-bottom: 20px; margin-bottom: 20px;">
        <h1>Your Company Name</h1>
        <p>123 Business Street | City, Country | Phone: +123 456 7890</p>
        <p>Email: info@yourcompany.com | Website: www.yourcompany.com</p>
    </div>',
    '<div class="invoice-footer" style="margin-top: 40px; padding-top: 20px; border-top: 1px solid #ddd; text-align: center; font-size: 12px;">
        <p><strong>Terms & Conditions:</strong></p>
        <p>Payment is due within 30 days. Late payments may incur additional charges.</p>
        <p>Thank you for your business!</p>
        <p style="margin-top: 20px;">Generated on {{Date}} at {{Time}}</p>
    </div>',
    TRUE,
    TRUE
);

-- Insert default email template for invoices
INSERT INTO "EmailTemplates" ("Name", "TemplateCode", "Subject", "BodyContent", "IsActive")
VALUES (
    'Invoice Email Template',
    'INVOICE_EMAIL',
    'Invoice #{{InvoiceNumber}} from Your Company',
    '<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;">
        <h2>Dear {{CustomerName}},</h2>
        <p>Thank you for your business. Please find your invoice attached.</p>
        <div style="background: #f5f5f5; padding: 15px; margin: 20px 0; border-left: 4px solid #4CAF50;">
            <p><strong>Invoice Number:</strong> {{InvoiceNumber}}</p>
            <p><strong>Date:</strong> {{DocDate}}</p>
            <p><strong>Amount Due:</strong> {{Currency}} {{Total}}</p>
            <p><strong>Due Date:</strong> {{DueDate}}</p>
        </div>
        <p>If you have any questions about this invoice, please contact us.</p>
        <p>Best regards,<br>Your Company Team</p>
        <hr style="margin: 30px 0; border: none; border-top: 1px solid #ddd;">
        <p style="font-size: 12px; color: #666;">
            This is an automated email. Please do not reply to this message.
        </p>
    </div>',
    TRUE
);

-- Verification queries
SELECT 'DocumentTemplates count:' as info, COUNT(*) as count FROM "DocumentTemplates";
SELECT 'DocumentAttachments count:' as info, COUNT(*) as count FROM "DocumentAttachments";
SELECT 'DocumentHistory count:' as info, COUNT(*) as count FROM "DocumentHistory";
SELECT 'DocumentSignatures count:' as info, COUNT(*) as count FROM "DocumentSignatures";
SELECT 'EmailTemplates count:' as info, COUNT(*) as count FROM "EmailTemplates";
