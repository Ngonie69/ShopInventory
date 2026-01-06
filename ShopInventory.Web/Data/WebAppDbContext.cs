using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Web.Data;

public class WebAppDbContext : DbContext
{
    public WebAppDbContext(DbContextOptions<WebAppDbContext> options) : base(options)
    {
    }

    public DbSet<CachedProduct> CachedProducts { get; set; }
    public DbSet<CachedPrice> CachedPrices { get; set; }
    public DbSet<CachedBusinessPartner> CachedBusinessPartners { get; set; }
    public DbSet<CachedWarehouse> CachedWarehouses { get; set; }
    public DbSet<CachedGLAccount> CachedGLAccounts { get; set; }
    public DbSet<CachedWarehouseStock> CachedWarehouseStocks { get; set; }
    public DbSet<CachedIncomingPayment> CachedIncomingPayments { get; set; }
    public DbSet<CachedInventoryTransfer> CachedInventoryTransfers { get; set; }
    public DbSet<CacheSyncInfo> CacheSyncInfo { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<AppSetting> AppSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // CachedProduct configuration
        modelBuilder.Entity<CachedProduct>(entity =>
        {
            entity.ToTable("CachedProducts");
            entity.HasKey(e => e.ItemCode);

            entity.Property(e => e.ItemCode)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.ItemName)
                .HasMaxLength(200);

            entity.Property(e => e.BarCode)
                .HasMaxLength(50);

            entity.Property(e => e.ItemType)
                .HasMaxLength(20);

            entity.Property(e => e.DefaultWarehouse)
                .HasMaxLength(20);

            entity.Property(e => e.UoM)
                .HasMaxLength(20);

            entity.Property(e => e.Price)
                .HasPrecision(18, 6);

            // Index for searching
            entity.HasIndex(e => e.ItemName);
            entity.HasIndex(e => e.BarCode);
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.LastSyncedAt);
        });

        // CachedPrice configuration
        modelBuilder.Entity<CachedPrice>(entity =>
        {
            entity.ToTable("CachedPrices");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ItemCode)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.ItemName)
                .HasMaxLength(200);

            entity.Property(e => e.Currency)
                .HasMaxLength(10);

            entity.Property(e => e.Price)
                .HasPrecision(18, 6);

            // Index for searching
            entity.HasIndex(e => e.ItemCode);
            entity.HasIndex(e => e.Currency);
            entity.HasIndex(e => new { e.ItemCode, e.Currency }).IsUnique();
        });

        // CachedBusinessPartner configuration
        modelBuilder.Entity<CachedBusinessPartner>(entity =>
        {
            entity.ToTable("CachedBusinessPartners");
            entity.HasKey(e => e.CardCode);

            entity.Property(e => e.CardCode)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.CardName)
                .HasMaxLength(200);

            entity.Property(e => e.Balance)
                .HasPrecision(18, 6);

            // Index for searching
            entity.HasIndex(e => e.CardName);
            entity.HasIndex(e => e.CardType);
            entity.HasIndex(e => e.IsActive);
        });

        // CachedWarehouse configuration
        modelBuilder.Entity<CachedWarehouse>(entity =>
        {
            entity.ToTable("CachedWarehouses");
            entity.HasKey(e => e.WarehouseCode);

            entity.Property(e => e.WarehouseCode)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.WarehouseName)
                .HasMaxLength(100);

            // Index for searching
            entity.HasIndex(e => e.WarehouseName);
            entity.HasIndex(e => e.IsActive);
        });

        // CachedGLAccount configuration
        modelBuilder.Entity<CachedGLAccount>(entity =>
        {
            entity.ToTable("CachedGLAccounts");
            entity.HasKey(e => e.Code);

            entity.Property(e => e.Code)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.Name)
                .HasMaxLength(200);

            entity.Property(e => e.Balance)
                .HasPrecision(18, 6);

            // Index for searching
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.AccountType);
            entity.HasIndex(e => e.IsActive);
        });

        // CachedWarehouseStock configuration
        modelBuilder.Entity<CachedWarehouseStock>(entity =>
        {
            entity.ToTable("CachedWarehouseStocks");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ItemCode)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.ItemName)
                .HasMaxLength(200);

            entity.Property(e => e.BarCode)
                .HasMaxLength(50);

            entity.Property(e => e.WarehouseCode)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.UoM)
                .HasMaxLength(20);

            entity.Property(e => e.InStock)
                .HasPrecision(18, 6);

            entity.Property(e => e.Committed)
                .HasPrecision(18, 6);

            entity.Property(e => e.Ordered)
                .HasPrecision(18, 6);

            entity.Property(e => e.Available)
                .HasPrecision(18, 6);

            // Unique index on ItemCode + WarehouseCode
            entity.HasIndex(e => new { e.WarehouseCode, e.ItemCode }).IsUnique();
            entity.HasIndex(e => e.WarehouseCode);
            entity.HasIndex(e => e.ItemCode);
            entity.HasIndex(e => e.BarCode);
            entity.HasIndex(e => e.LastSyncedAt);
        });

        // CachedIncomingPayment configuration
        modelBuilder.Entity<CachedIncomingPayment>(entity =>
        {
            entity.ToTable("CachedIncomingPayments");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.CardCode)
                .HasMaxLength(50);

            entity.Property(e => e.CardName)
                .HasMaxLength(200);

            entity.Property(e => e.DocCurrency)
                .HasMaxLength(10);

            entity.Property(e => e.CashSum)
                .HasPrecision(18, 6);

            entity.Property(e => e.CheckSum)
                .HasPrecision(18, 6);

            entity.Property(e => e.TransferSum)
                .HasPrecision(18, 6);

            entity.Property(e => e.CreditSum)
                .HasPrecision(18, 6);

            entity.Property(e => e.DocTotal)
                .HasPrecision(18, 6);

            entity.Property(e => e.Remarks)
                .HasMaxLength(500);

            entity.Property(e => e.TransferReference)
                .HasMaxLength(100);

            entity.Property(e => e.TransferAccount)
                .HasMaxLength(50);

            // Indexes
            entity.HasIndex(e => e.DocEntry).IsUnique();
            entity.HasIndex(e => e.DocNum);
            entity.HasIndex(e => e.CardCode);
            entity.HasIndex(e => e.DocDate);
            entity.HasIndex(e => e.LastSyncedAt);
        });

        // CachedInventoryTransfer configuration
        modelBuilder.Entity<CachedInventoryTransfer>(entity =>
        {
            entity.ToTable("CachedInventoryTransfers");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FromWarehouse)
                .HasMaxLength(20);

            entity.Property(e => e.ToWarehouse)
                .HasMaxLength(20);

            entity.Property(e => e.Comments)
                .HasMaxLength(500);

            // Indexes
            entity.HasIndex(e => e.DocEntry).IsUnique();
            entity.HasIndex(e => e.DocNum);
            entity.HasIndex(e => e.FromWarehouse);
            entity.HasIndex(e => e.ToWarehouse);
            entity.HasIndex(e => e.DocDate);
            entity.HasIndex(e => e.LastSyncedAt);
        });

        // CacheSyncInfo configuration
        modelBuilder.Entity<CacheSyncInfo>(entity =>
        {
            entity.ToTable("CacheSyncInfo");
            entity.HasKey(e => e.CacheKey);

            entity.Property(e => e.CacheKey)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.LastError)
                .HasMaxLength(500);
        });

        // AuditLog configuration
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Username)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.UserRole)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.Action)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.EntityType)
                .HasMaxLength(100);

            entity.Property(e => e.EntityId)
                .HasMaxLength(100);

            entity.Property(e => e.Details)
                .HasMaxLength(1000);

            entity.Property(e => e.IpAddress)
                .HasMaxLength(50);

            entity.Property(e => e.UserAgent)
                .HasMaxLength(500);

            entity.Property(e => e.PageUrl)
                .HasMaxLength(500);

            entity.Property(e => e.ErrorMessage)
                .HasMaxLength(1000);

            // Indexes for querying
            entity.HasIndex(e => e.Username);
            entity.HasIndex(e => e.Action);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.Timestamp, e.Username });
        });

        // AppSetting configuration
        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Category)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(e => e.Key)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.Value)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(e => e.DataType)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.Description)
                .HasMaxLength(500);

            entity.Property(e => e.LastModifiedBy)
                .HasMaxLength(100);

            // Unique index on key
            entity.HasIndex(e => e.Key).IsUnique();
            entity.HasIndex(e => e.Category);
        });

        // Seed default warehouses for fast initial load
        SeedDefaultWarehouses(modelBuilder);
    }

    private static void SeedDefaultWarehouses(ModelBuilder modelBuilder)
    {
        var seedTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<CachedWarehouse>().HasData(
            new CachedWarehouse
            {
                WarehouseCode = "01",
                WarehouseName = "General Warehouse",
                Location = "Main",
                IsActive = true,
                LastSyncedAt = seedTime
            },
            new CachedWarehouse
            {
                WarehouseCode = "02",
                WarehouseName = "Warehouse 02",
                Location = "Secondary",
                IsActive = true,
                LastSyncedAt = seedTime
            },
            new CachedWarehouse
            {
                WarehouseCode = "03",
                WarehouseName = "Warehouse 03",
                Location = "Tertiary",
                IsActive = true,
                LastSyncedAt = seedTime
            }
        );
    }
}
