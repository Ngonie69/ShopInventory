using Microsoft.EntityFrameworkCore;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Data;

/// <summary>
/// Application database context for Entity Framework Core with PostgreSQL
/// </summary>
public class ApplicationDbContext : DbContext
{
  public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
      : base(options)
  {
  }

  // Authentication tables
  public DbSet<User> Users { get; set; }
  public DbSet<RefreshToken> RefreshTokens { get; set; }
  public DbSet<PasswordResetToken> PasswordResetTokens { get; set; }

  // Product tables
  public DbSet<ProductEntity> Products { get; set; }
  public DbSet<ProductBatchEntity> ProductBatches { get; set; }

  // Invoice tables
  public DbSet<InvoiceEntity> Invoices { get; set; }
  public DbSet<InvoiceLineEntity> InvoiceLines { get; set; }
  public DbSet<InvoiceLineBatchEntity> InvoiceLineBatches { get; set; }

  // Inventory Transfer tables
  public DbSet<InventoryTransferEntity> InventoryTransfers { get; set; }
  public DbSet<InventoryTransferLineEntity> InventoryTransferLines { get; set; }
  public DbSet<InventoryTransferLineBatchEntity> InventoryTransferLineBatches { get; set; }

  // Item Price tables
  public DbSet<ItemPriceEntity> ItemPrices { get; set; }
  public DbSet<PriceListEntity> PriceLists { get; set; }

  // Incoming Payment tables
  public DbSet<IncomingPaymentEntity> IncomingPayments { get; set; }
  public DbSet<IncomingPaymentInvoiceEntity> IncomingPaymentInvoices { get; set; }
  public DbSet<IncomingPaymentCheckEntity> IncomingPaymentChecks { get; set; }
  public DbSet<IncomingPaymentCreditCardEntity> IncomingPaymentCreditCards { get; set; }

  // Notification and Queue tables
  public DbSet<Notification> Notifications { get; set; }
  public DbSet<OfflineQueueItem> OfflineQueueItems { get; set; }
  public DbSet<EmailQueueItem> EmailQueueItems { get; set; }
  public DbSet<SapConnectionLog> SapConnectionLogs { get; set; }
  public DbSet<UserNotificationSettings> UserNotificationSettings { get; set; }

  // Webhook tables
  public DbSet<Webhook> Webhooks { get; set; }
  public DbSet<WebhookDelivery> WebhookDeliveries { get; set; }

  // Payment tables
  public DbSet<PaymentTransaction> PaymentTransactions { get; set; }
  public DbSet<PaymentGatewayConfig> PaymentGatewayConfigs { get; set; }

  // Audit tables
  public DbSet<AuditLog> AuditLogs { get; set; }

  // Sales Order tables
  public DbSet<SalesOrderEntity> SalesOrders { get; set; }
  public DbSet<SalesOrderLineEntity> SalesOrderLines { get; set; }

  // Credit Note tables
  public DbSet<CreditNoteEntity> CreditNotes { get; set; }
  public DbSet<CreditNoteLineEntity> CreditNoteLines { get; set; }

  // System tables
  public DbSet<ExchangeRateEntity> ExchangeRates { get; set; }
  public DbSet<SystemConfigEntity> SystemConfigs { get; set; }
  public DbSet<BackupEntity> Backups { get; set; }
  public DbSet<ApiRateLimitEntity> ApiRateLimits { get; set; }
  public DbSet<UserPermissionEntity> UserPermissions { get; set; }
  public DbSet<RoleEntity> Roles { get; set; }
  public DbSet<RolePermissionEntity> RolePermissions { get; set; }

  // Document Management tables
  public DbSet<DocumentTemplateEntity> DocumentTemplates { get; set; }
  public DbSet<DocumentAttachmentEntity> DocumentAttachments { get; set; }
  public DbSet<DocumentHistoryEntity> DocumentHistory { get; set; }
  public DbSet<DocumentSignatureEntity> DocumentSignatures { get; set; }
  public DbSet<EmailTemplateEntity> EmailTemplates { get; set; }

  // Stock Reservation tables (for desktop app integration)
  public DbSet<StockReservationEntity> StockReservations { get; set; }
  public DbSet<StockReservationLineEntity> StockReservationLines { get; set; }
  public DbSet<StockReservationBatchEntity> StockReservationBatches { get; set; }

  // Invoice Queue for batch posting
  public DbSet<InvoiceQueueEntity> InvoiceQueue { get; set; }

  // Inventory Transfer Queue for batch posting
  public DbSet<InventoryTransferQueueEntity> InventoryTransferQueue { get; set; }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    base.OnModelCreating(modelBuilder);

    // User configuration
    modelBuilder.Entity<User>(entity =>
    {
      entity.ToTable("Users");

      entity.HasIndex(u => u.Username)
                .IsUnique();

      entity.HasIndex(u => u.Email)
                .IsUnique();

      entity.Property(u => u.Username)
                .IsRequired()
                .HasMaxLength(50);

      entity.Property(u => u.PasswordHash)
                .IsRequired()
                .HasMaxLength(255);

      entity.Property(u => u.Role)
                .IsRequired()
                .HasMaxLength(50);
    });

    // RefreshToken configuration
    modelBuilder.Entity<RefreshToken>(entity =>
    {
      entity.ToTable("RefreshTokens");

      entity.HasIndex(rt => rt.Token)
                .IsUnique();

      entity.HasOne(rt => rt.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
    });

    // PasswordResetToken configuration
    modelBuilder.Entity<PasswordResetToken>(entity =>
    {
      entity.ToTable("PasswordResetTokens");

      entity.HasIndex(prt => prt.TokenHash)
                .IsUnique();

      entity.HasIndex(prt => prt.ExpiresAt);

      entity.Property(prt => prt.TokenHash)
                .IsRequired()
                .HasMaxLength(128);

      entity.HasOne(prt => prt.User)
                .WithMany(u => u.PasswordResetTokens)
                .HasForeignKey(prt => prt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
    });

    // Product configuration with CHECK constraints to prevent negative quantities
    modelBuilder.Entity<ProductEntity>(entity =>
    {
      entity.HasIndex(p => p.ItemCode)
                .IsUnique();

      entity.HasIndex(p => p.BarCode);

      entity.HasMany(p => p.Batches)
                .WithOne(b => b.Product)
                .HasForeignKey(b => b.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

      entity.HasMany(p => p.Prices)
                .WithOne(pr => pr.Product)
                .HasForeignKey(pr => pr.ProductId)
                .OnDelete(DeleteBehavior.SetNull);

      // CHECK constraints to prevent negative quantities - CRITICAL for stock variance prevention
      entity.ToTable(t =>
          {
            t.HasCheckConstraint("CK_Products_QuantityOnStock_NonNegative", "\"QuantityOnStock\" >= 0");
            t.HasCheckConstraint("CK_Products_QuantityOrderedFromVendors_NonNegative", "\"QuantityOrderedFromVendors\" >= 0");
            t.HasCheckConstraint("CK_Products_QuantityOrderedByCustomers_NonNegative", "\"QuantityOrderedByCustomers\" >= 0");
          });
    });

    // Product Batch configuration with CHECK constraint
    modelBuilder.Entity<ProductBatchEntity>(entity =>
    {
      entity.HasIndex(b => new { b.ProductId, b.BatchNumber })
                .IsUnique();

      entity.HasIndex(b => b.BatchNumber);

      // CHECK constraint to prevent negative batch quantities
      entity.ToTable(t => t.HasCheckConstraint("CK_ProductBatches_Quantity_NonNegative", "\"Quantity\" >= 0"));
    });

    // Invoice configuration
    modelBuilder.Entity<InvoiceEntity>(entity =>
    {
      entity.HasIndex(i => i.SAPDocEntry);
      entity.HasIndex(i => i.SAPDocNum);
      entity.HasIndex(i => i.CardCode);
      entity.HasIndex(i => i.DocDate);
      entity.HasIndex(i => i.Status);

      entity.HasMany(i => i.DocumentLines)
                .WithOne(l => l.Invoice)
                .HasForeignKey(l => l.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

      // CHECK constraints for invoice amounts
      entity.ToTable(t =>
          {
            t.HasCheckConstraint("CK_Invoices_DocTotal_NonNegative", "\"DocTotal\" >= 0");
            t.HasCheckConstraint("CK_Invoices_VatSum_NonNegative", "\"VatSum\" >= 0");
          });
    });

    // Invoice Line configuration with CHECK constraints
    modelBuilder.Entity<InvoiceLineEntity>(entity =>
    {
      entity.HasIndex(l => l.ItemCode);

      entity.HasOne(l => l.Product)
                .WithMany(p => p.InvoiceLines)
                .HasForeignKey(l => l.ProductId)
                .OnDelete(DeleteBehavior.SetNull);

      entity.HasMany(l => l.BatchNumbers)
                .WithOne(b => b.InvoiceLine)
                .HasForeignKey(b => b.InvoiceLineId)
                .OnDelete(DeleteBehavior.Cascade);

      // CHECK constraints to prevent negative quantities and prices
      entity.ToTable(t =>
          {
            t.HasCheckConstraint("CK_InvoiceLines_Quantity_Positive", "\"Quantity\" > 0");
            t.HasCheckConstraint("CK_InvoiceLines_UnitPrice_NonNegative", "\"UnitPrice\" >= 0");
            t.HasCheckConstraint("CK_InvoiceLines_LineTotal_NonNegative", "\"LineTotal\" >= 0");
            t.HasCheckConstraint("CK_InvoiceLines_DiscountPercent_Valid", "\"DiscountPercent\" >= 0 AND \"DiscountPercent\" <= 100");
          });
    });

    // Invoice Line Batch configuration with CHECK constraint
    modelBuilder.Entity<InvoiceLineBatchEntity>(entity =>
    {
      // CHECK constraint to prevent negative batch quantities
      entity.ToTable(t => t.HasCheckConstraint("CK_InvoiceLineBatches_Quantity_Positive", "\"Quantity\" > 0"));
    });

    // Inventory Transfer configuration
    modelBuilder.Entity<InventoryTransferEntity>(entity =>
    {
      entity.HasIndex(t => t.SAPDocEntry);
      entity.HasIndex(t => t.SAPDocNum);
      entity.HasIndex(t => t.DocDate);
      entity.HasIndex(t => t.Status);

      entity.HasMany(t => t.StockTransferLines)
                .WithOne(l => l.InventoryTransfer)
                .HasForeignKey(l => l.InventoryTransferId)
                .OnDelete(DeleteBehavior.Cascade);
    });

    // Inventory Transfer Line configuration with CHECK constraint
    modelBuilder.Entity<InventoryTransferLineEntity>(entity =>
    {
      entity.HasIndex(l => l.ItemCode);

      entity.HasOne(l => l.Product)
                .WithMany(p => p.TransferLines)
                .HasForeignKey(l => l.ProductId)
                .OnDelete(DeleteBehavior.SetNull);

      entity.HasMany(l => l.BatchNumbers)
                .WithOne(b => b.InventoryTransferLine)
                .HasForeignKey(b => b.InventoryTransferLineId)
                .OnDelete(DeleteBehavior.Cascade);

      // CHECK constraint to prevent negative transfer quantities
      entity.ToTable(t => t.HasCheckConstraint("CK_InventoryTransferLines_Quantity_Positive", "\"Quantity\" > 0"));
    });

    // Inventory Transfer Line Batch configuration with CHECK constraint
    modelBuilder.Entity<InventoryTransferLineBatchEntity>(entity =>
    {
      // CHECK constraint to prevent negative batch quantities
      entity.ToTable(t => t.HasCheckConstraint("CK_InventoryTransferLineBatches_Quantity_Positive", "\"Quantity\" > 0"));
    });

    // Item Price configuration with CHECK constraint
    modelBuilder.Entity<ItemPriceEntity>(entity =>
    {
      entity.HasIndex(p => new { p.ItemCode, p.PriceList })
                .IsUnique();

      entity.HasIndex(p => p.ItemCode);
      entity.HasIndex(p => p.PriceList);

      // CHECK constraint to prevent negative prices
      entity.ToTable(t => t.HasCheckConstraint("CK_ItemPrices_Price_NonNegative", "\"Price\" >= 0"));
    });

    // Price List configuration
    modelBuilder.Entity<PriceListEntity>(entity =>
    {
      entity.HasIndex(p => p.ListNum).IsUnique();
      entity.HasIndex(p => p.IsActive);
    });

    // Incoming Payment configuration with CHECK constraints
    modelBuilder.Entity<IncomingPaymentEntity>(entity =>
    {
      entity.HasIndex(p => p.SAPDocEntry);
      entity.HasIndex(p => p.SAPDocNum);
      entity.HasIndex(p => p.CardCode);
      entity.HasIndex(p => p.DocDate);
      entity.HasIndex(p => p.Status);

      entity.HasMany(p => p.PaymentInvoices)
                .WithOne(i => i.IncomingPayment)
                .HasForeignKey(i => i.IncomingPaymentId)
                .OnDelete(DeleteBehavior.Cascade);

      entity.HasMany(p => p.PaymentChecks)
                .WithOne(c => c.IncomingPayment)
                .HasForeignKey(c => c.IncomingPaymentId)
                .OnDelete(DeleteBehavior.Cascade);

      entity.HasMany(p => p.PaymentCreditCards)
                .WithOne(cc => cc.IncomingPayment)
                .HasForeignKey(cc => cc.IncomingPaymentId)
                .OnDelete(DeleteBehavior.Cascade);

      // CHECK constraints to prevent negative payment amounts
      entity.ToTable(t =>
          {
            t.HasCheckConstraint("CK_IncomingPayments_CashSum_NonNegative", "\"CashSum\" >= 0");
            t.HasCheckConstraint("CK_IncomingPayments_CheckSum_NonNegative", "\"CheckSum\" >= 0");
            t.HasCheckConstraint("CK_IncomingPayments_TransferSum_NonNegative", "\"TransferSum\" >= 0");
            t.HasCheckConstraint("CK_IncomingPayments_CreditSum_NonNegative", "\"CreditSum\" >= 0");
            t.HasCheckConstraint("CK_IncomingPayments_DocTotal_NonNegative", "\"DocTotal\" >= 0");
          });
    });

    // Incoming Payment Invoice configuration with CHECK constraint
    modelBuilder.Entity<IncomingPaymentInvoiceEntity>(entity =>
    {
      entity.HasOne(pi => pi.Invoice)
                .WithMany(i => i.PaymentInvoices)
                .HasForeignKey(pi => pi.InvoiceId)
                .OnDelete(DeleteBehavior.SetNull);

      // CHECK constraint to prevent negative applied amounts
      entity.ToTable(t => t.HasCheckConstraint("CK_IncomingPaymentInvoices_SumApplied_NonNegative", "\"SumApplied\" >= 0"));
    });

    // Incoming Payment Check configuration with CHECK constraint
    modelBuilder.Entity<IncomingPaymentCheckEntity>(entity =>
    {
      // CHECK constraint to prevent negative check amounts
      entity.ToTable(t => t.HasCheckConstraint("CK_IncomingPaymentChecks_CheckSum_NonNegative", "\"CheckSum\" >= 0"));
    });

    // Incoming Payment Credit Card configuration with CHECK constraint
    modelBuilder.Entity<IncomingPaymentCreditCardEntity>(entity =>
    {
      // CHECK constraint to prevent negative credit amounts
      entity.ToTable(t => t.HasCheckConstraint("CK_IncomingPaymentCreditCards_CreditSum_NonNegative", "\"CreditSum\" >= 0"));
    });

    // Notification configuration
    modelBuilder.Entity<Notification>(entity =>
    {
      entity.ToTable("Notifications");
      entity.HasKey(n => n.Id);

      entity.HasIndex(n => n.UserId);
      entity.HasIndex(n => n.TargetUsername);
      entity.HasIndex(n => n.TargetRole);
      entity.HasIndex(n => n.IsRead);
      entity.HasIndex(n => n.CreatedAt);
      entity.HasIndex(n => n.Category);

      entity.HasOne(n => n.User)
                  .WithMany()
                  .HasForeignKey(n => n.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
    });

    // Offline Queue configuration
    modelBuilder.Entity<OfflineQueueItem>(entity =>
    {
      entity.ToTable("OfflineQueueItems");
      entity.HasKey(q => q.Id);

      entity.HasIndex(q => q.Status);
      entity.HasIndex(q => q.TransactionType);
      entity.HasIndex(q => q.CreatedAt);
      entity.HasIndex(q => q.NextRetryAt);
      entity.HasIndex(q => q.Priority);
    });

    // Email Queue configuration
    modelBuilder.Entity<EmailQueueItem>(entity =>
    {
      entity.ToTable("EmailQueueItems");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.Status);
      entity.HasIndex(e => e.CreatedAt);
    });

    // SAP Connection Log configuration
    modelBuilder.Entity<SapConnectionLog>(entity =>
    {
      entity.ToTable("SapConnectionLogs");
      entity.HasKey(l => l.Id);

      entity.HasIndex(l => l.CheckedAt);
      entity.HasIndex(l => l.IsSuccess);
    });

    // User Notification Settings configuration
    modelBuilder.Entity<UserNotificationSettings>(entity =>
    {
      entity.ToTable("UserNotificationSettings");
      entity.HasKey(s => s.UserId);

      entity.HasOne(s => s.User)
                  .WithOne()
                  .HasForeignKey<UserNotificationSettings>(s => s.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
    });

    // Webhook configuration
    modelBuilder.Entity<Webhook>(entity =>
    {
      entity.ToTable("Webhooks");
      entity.HasKey(w => w.Id);

      entity.HasIndex(w => w.IsActive);
      entity.HasIndex(w => w.Name);

      entity.Property(w => w.Name)
                  .IsRequired()
                  .HasMaxLength(100);

      entity.Property(w => w.Url)
                  .IsRequired()
                  .HasMaxLength(500);

      entity.Property(w => w.Events)
                  .IsRequired()
                  .HasMaxLength(500);
    });

    // Webhook Delivery configuration
    modelBuilder.Entity<WebhookDelivery>(entity =>
    {
      entity.ToTable("WebhookDeliveries");
      entity.HasKey(d => d.Id);

      entity.HasIndex(d => d.CreatedAt);
      entity.HasIndex(d => d.IsSuccess);

      entity.HasOne(d => d.Webhook)
                  .WithMany()
                  .HasForeignKey(d => d.WebhookId)
                  .OnDelete(DeleteBehavior.Cascade);
    });

    // Payment Transaction configuration
    modelBuilder.Entity<PaymentTransaction>(entity =>
    {
      entity.ToTable("PaymentTransactions");
      entity.HasKey(p => p.Id);

      entity.HasIndex(p => p.ExternalTransactionId);
      entity.HasIndex(p => p.Status);
      entity.HasIndex(p => p.Provider);
      entity.HasIndex(p => p.CreatedAt);
      entity.HasIndex(p => p.CustomerCode);

      entity.Property(p => p.Provider)
                  .IsRequired()
                  .HasMaxLength(50);

      entity.Property(p => p.Amount)
                  .HasPrecision(18, 2);

      entity.Property(p => p.Status)
                  .IsRequired()
                  .HasMaxLength(20);
    });

    // Payment Gateway Config configuration
    modelBuilder.Entity<PaymentGatewayConfig>(entity =>
    {
      entity.ToTable("PaymentGatewayConfigs");
      entity.HasKey(c => c.Id);

      entity.HasIndex(c => c.Provider).IsUnique();

      entity.Property(c => c.Provider)
                  .IsRequired()
                  .HasMaxLength(50);
    });

    // Audit Log configuration
    modelBuilder.Entity<AuditLog>(entity =>
    {
      entity.ToTable("AuditLogs");
      entity.HasKey(a => a.Id);

      entity.HasIndex(a => a.UserId);
      entity.HasIndex(a => a.Username);
      entity.HasIndex(a => a.Action);
      entity.HasIndex(a => a.EntityType);
      entity.HasIndex(a => a.Timestamp);
      entity.HasIndex(a => a.IsSuccess);
    });

    // Sales Order configuration
    modelBuilder.Entity<SalesOrderEntity>(entity =>
    {
      entity.ToTable("SalesOrders");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.OrderNumber).IsUnique();
      entity.HasIndex(e => e.CardCode);
      entity.HasIndex(e => e.Status);
      entity.HasIndex(e => e.OrderDate);
      entity.HasIndex(e => e.SAPDocEntry);

      entity.HasMany(e => e.Lines)
            .WithOne(l => l.SalesOrder)
            .HasForeignKey(l => l.SalesOrderId)
            .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Invoice)
            .WithMany()
            .HasForeignKey(e => e.InvoiceId)
            .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.ApprovedByUser)
            .WithMany()
            .HasForeignKey(e => e.ApprovedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    });

    // Sales Order Line configuration
    modelBuilder.Entity<SalesOrderLineEntity>(entity =>
    {
      entity.ToTable("SalesOrderLines");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.ItemCode);

      entity.HasOne(e => e.Product)
            .WithMany()
            .HasForeignKey(e => e.ProductId)
            .OnDelete(DeleteBehavior.SetNull);
    });

    // Credit Note configuration
    modelBuilder.Entity<CreditNoteEntity>(entity =>
    {
      entity.ToTable("CreditNotes");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.CreditNoteNumber).IsUnique();
      entity.HasIndex(e => e.CardCode);
      entity.HasIndex(e => e.Status);
      entity.HasIndex(e => e.CreditNoteDate);
      entity.HasIndex(e => e.SAPDocEntry);

      entity.HasMany(e => e.Lines)
            .WithOne(l => l.CreditNote)
            .HasForeignKey(l => l.CreditNoteId)
            .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.OriginalInvoice)
            .WithMany()
            .HasForeignKey(e => e.OriginalInvoiceId)
            .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.ApprovedByUser)
            .WithMany()
            .HasForeignKey(e => e.ApprovedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    });

    // Credit Note Line configuration
    modelBuilder.Entity<CreditNoteLineEntity>(entity =>
    {
      entity.ToTable("CreditNoteLines");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.ItemCode);

      entity.HasOne(e => e.Product)
            .WithMany()
            .HasForeignKey(e => e.ProductId)
            .OnDelete(DeleteBehavior.SetNull);
    });

    // Exchange Rate configuration
    modelBuilder.Entity<ExchangeRateEntity>(entity =>
    {
      entity.ToTable("ExchangeRates");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => new { e.FromCurrency, e.ToCurrency, e.EffectiveDate });
      entity.HasIndex(e => e.IsActive);

      entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    });

    // System Config configuration
    modelBuilder.Entity<SystemConfigEntity>(entity =>
    {
      entity.ToTable("SystemConfigs");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.Key).IsUnique();
      entity.HasIndex(e => e.Category);
    });

    // Backup configuration
    modelBuilder.Entity<BackupEntity>(entity =>
    {
      entity.ToTable("Backups");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.StartedAt);
      entity.HasIndex(e => e.Status);

      entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    });

    // API Rate Limit configuration
    modelBuilder.Entity<ApiRateLimitEntity>(entity =>
    {
      entity.ToTable("ApiRateLimits");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.ClientId);
      entity.HasIndex(e => e.IsBlocked);
      entity.HasIndex(e => e.LastRequestAt);
    });

    // User Permission configuration
    modelBuilder.Entity<UserPermissionEntity>(entity =>
    {
      entity.ToTable("UserPermissions");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => new { e.UserId, e.Permission }).IsUnique();

      entity.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.AssignedByUser)
            .WithMany()
            .HasForeignKey(e => e.AssignedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    });

    // Role configuration
    modelBuilder.Entity<RoleEntity>(entity =>
    {
      entity.ToTable("Roles");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.Name).IsUnique();

      entity.HasMany(e => e.Permissions)
            .WithOne(p => p.Role)
            .HasForeignKey(p => p.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    });

    // Role Permission configuration
    modelBuilder.Entity<RolePermissionEntity>(entity =>
    {
      entity.ToTable("RolePermissions");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => new { e.RoleId, e.Permission }).IsUnique();
    });

    // Stock Reservation configuration (for desktop app integration)
    modelBuilder.Entity<StockReservationEntity>(entity =>
    {
      entity.ToTable("StockReservations");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => e.ReservationId).IsUnique();
      entity.HasIndex(e => e.ExternalReferenceId).IsUnique();
      entity.HasIndex(e => new { e.Status, e.ExpiresAt });
      entity.HasIndex(e => e.CardCode);
      entity.HasIndex(e => e.SourceSystem);

      entity.HasMany(r => r.Lines)
            .WithOne(l => l.Reservation)
            .HasForeignKey(l => l.ReservationId)
            .OnDelete(DeleteBehavior.Cascade);

      // CHECK constraint for non-negative total value
      entity.ToTable(t => t.HasCheckConstraint("CK_StockReservations_TotalValue_NonNegative", "\"TotalValue\" >= 0"));
    });

    // Stock Reservation Line configuration
    modelBuilder.Entity<StockReservationLineEntity>(entity =>
    {
      entity.ToTable("StockReservationLines");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => new { e.ItemCode, e.WarehouseCode });

      entity.HasMany(l => l.BatchAllocations)
            .WithOne(b => b.ReservationLine)
            .HasForeignKey(b => b.ReservationLineId)
            .OnDelete(DeleteBehavior.Cascade);

      // CHECK constraints to prevent negative quantities
      entity.ToTable(t =>
      {
        t.HasCheckConstraint("CK_StockReservationLines_ReservedQuantity_Positive", "\"ReservedQuantity\" > 0");
        t.HasCheckConstraint("CK_StockReservationLines_UnitPrice_NonNegative", "\"UnitPrice\" >= 0");
        t.HasCheckConstraint("CK_StockReservationLines_LineTotal_NonNegative", "\"LineTotal\" >= 0");
      });
    });

    // Stock Reservation Batch configuration
    modelBuilder.Entity<StockReservationBatchEntity>(entity =>
    {
      entity.ToTable("StockReservationBatches");
      entity.HasKey(e => e.Id);

      entity.HasIndex(e => new { e.ItemCode, e.WarehouseCode, e.BatchNumber });

      // CHECK constraint to prevent negative batch quantities
      entity.ToTable(t => t.HasCheckConstraint("CK_StockReservationBatches_ReservedQuantity_Positive", "\"ReservedQuantity\" > 0"));
    });
  }
}
