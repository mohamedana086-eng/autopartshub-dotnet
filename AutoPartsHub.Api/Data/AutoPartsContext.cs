using System;
using System.Collections.Generic;
using AutoPartsHub.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Data;

public partial class AutoPartsContext : DbContext
{
    public AutoPartsContext(DbContextOptions<AutoPartsContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Cart> Carts { get; set; }

    public virtual DbSet<CartItem> CartItems { get; set; }

    public virtual DbSet<Client> Clients { get; set; }

    public virtual DbSet<ClientCategory> ClientCategories { get; set; }

    public virtual DbSet<Currency> Currencies { get; set; }

    public virtual DbSet<Fitment> Fitments { get; set; }

    public virtual DbSet<Interchange> Interchanges { get; set; }

    public virtual DbSet<Manufacturer> Manufacturers { get; set; }

    public virtual DbSet<MarkupRule> MarkupRules { get; set; }

    public virtual DbSet<Notification> Notifications { get; set; }

    public virtual DbSet<Order> Orders { get; set; }

    public virtual DbSet<OrderItem> OrderItems { get; set; }

    public virtual DbSet<OrderItemAllocation> OrderItemAllocations { get; set; }

    public virtual DbSet<PriceList> PriceLists { get; set; }

    public virtual DbSet<PriceListItem> PriceListItems { get; set; }

    public virtual DbSet<Product> Products { get; set; }

    public virtual DbSet<ProductImage> ProductImages { get; set; }

    public virtual DbSet<RetailOutlet> RetailOutlets { get; set; }

    public virtual DbSet<StockLevel> StockLevels { get; set; }

    public virtual DbSet<Supplier> Suppliers { get; set; }

    public virtual DbSet<VehicleMake> VehicleMakes { get; set; }

    public virtual DbSet<VehicleModel> VehicleModels { get; set; }

    public virtual DbSet<VehicleSystem> VehicleSystems { get; set; }

    public virtual DbSet<VehicleVariant> VehicleVariants { get; set; }

    public virtual DbSet<VerificationToken> VerificationTokens { get; set; }

    public virtual DbSet<Warehouse> Warehouses { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Cart>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Cart_pkey");

            entity.ToTable("Cart");

            entity.HasIndex(e => e.ClientId, "Cart_clientId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ClientId).HasColumnName("clientId");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");

            entity.HasOne(d => d.Client).WithOne(p => p.Cart)
                .HasForeignKey<Cart>(d => d.ClientId)
                .HasConstraintName("Cart_clientId_fkey");
        });

        modelBuilder.Entity<CartItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("CartItem_pkey");

            entity.ToTable("CartItem");

            entity.HasIndex(e => new { e.CartId, e.ProductId }, "CartItem_cartId_productId_key").IsUnique();

            entity.HasIndex(e => e.ProductId, "CartItem_productId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AddedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("addedAt");
            entity.Property(e => e.CartId).HasColumnName("cartId");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.Quantity)
                .HasDefaultValue(1)
                .HasColumnName("quantity");

            entity.HasOne(d => d.Cart).WithMany(p => p.CartItems)
                .HasForeignKey(d => d.CartId)
                .HasConstraintName("CartItem_cartId_fkey");

            entity.HasOne(d => d.Product).WithMany(p => p.CartItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("CartItem_productId_fkey");
        });

        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Client_pkey");

            entity.ToTable("Client");

            entity.HasIndex(e => e.CurrencyId, "Client_currencyId_idx");

            entity.HasIndex(e => e.Email, "Client_email_key").IsUnique();

            entity.HasIndex(e => e.SalesManagerId, "Client_salesManagerId_idx");

            entity.HasIndex(e => e.SupplierId, "Client_supplierId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.CategoryId).HasColumnName("categoryId");
            entity.Property(e => e.City).HasColumnName("city");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.CurrencyId).HasColumnName("currencyId");
            entity.Property(e => e.DiscountPercent).HasColumnName("discountPercent");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.EmailConfirmedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("emailConfirmedAt");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.PasswordHash).HasColumnName("passwordHash");
            entity.Property(e => e.Role)
                .HasDefaultValueSql("'RETAIL'::text")
                .HasColumnName("role");
            entity.Property(e => e.SalesManagerId).HasColumnName("salesManagerId");
            entity.Property(e => e.SupplierId).HasColumnName("supplierId");

            entity.HasOne(d => d.Category).WithMany(p => p.Clients)
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Client_categoryId_fkey");

            entity.HasOne(d => d.Currency).WithMany(p => p.Clients)
                .HasForeignKey(d => d.CurrencyId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Client_currencyId_fkey");

            entity.HasOne(d => d.SalesManager).WithMany(p => p.InverseSalesManager)
                .HasForeignKey(d => d.SalesManagerId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Client_salesManagerId_fkey");

            entity.HasOne(d => d.Supplier).WithMany(p => p.Clients)
                .HasForeignKey(d => d.SupplierId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Client_supplierId_fkey");
        });

        modelBuilder.Entity<ClientCategory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ClientCategory_pkey");

            entity.ToTable("ClientCategory");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.MarkupPercent).HasColumnName("markupPercent");
            entity.Property(e => e.MinOrderAmount).HasColumnName("minOrderAmount");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.RequiresApproval).HasColumnName("requiresApproval");
            entity.Property(e => e.ShelfLifeDays)
                .HasDefaultValue(1)
                .HasColumnName("shelfLifeDays");
        });

        modelBuilder.Entity<Currency>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Currency_pkey");

            entity.ToTable("Currency");

            entity.HasIndex(e => e.Code, "Currency_code_key").IsUnique();

            entity.HasIndex(e => e.IsBase, "Currency_single_base")
                .IsUnique()
                .HasFilter("(\"isBase\" = true)");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Active)
                .HasDefaultValue(true)
                .HasColumnName("active");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.IsBase).HasColumnName("isBase");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Rate)
                .HasDefaultValue(1.0)
                .HasColumnName("rate");
            entity.Property(e => e.Symbol).HasColumnName("symbol");
        });

        modelBuilder.Entity<Fitment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Fitment_pkey");

            entity.ToTable("Fitment");

            entity.HasIndex(e => new { e.ProductId, e.VariantId }, "Fitment_productId_variantId_key").IsUnique();

            entity.HasIndex(e => e.VariantId, "Fitment_variantId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.VariantId).HasColumnName("variantId");

            entity.HasOne(d => d.Product).WithMany(p => p.Fitments)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Fitment_productId_fkey");

            entity.HasOne(d => d.Variant).WithMany(p => p.Fitments)
                .HasForeignKey(d => d.VariantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Fitment_variantId_fkey");
        });

        modelBuilder.Entity<Interchange>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Interchange_pkey");

            entity.ToTable("Interchange");

            entity.HasIndex(e => e.IsOem, "Interchange_isOEM_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ExactMatch).HasColumnName("exactMatch");
            entity.Property(e => e.IsOem).HasColumnName("isOEM");
            entity.Property(e => e.SourceId).HasColumnName("sourceId");
            entity.Property(e => e.TargetManufacturer).HasColumnName("targetManufacturer");
            entity.Property(e => e.TargetPartNo).HasColumnName("targetPartNo");

            entity.HasOne(d => d.Source).WithMany(p => p.Interchanges)
                .HasForeignKey(d => d.SourceId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Interchange_sourceId_fkey");
        });

        modelBuilder.Entity<Manufacturer>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Manufacturer_pkey");

            entity.ToTable("Manufacturer");

            entity.HasIndex(e => e.Name, "Manufacturer_name_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.IsOem).HasColumnName("isOEM");
            entity.Property(e => e.Name).HasColumnName("name");
        });

        modelBuilder.Entity<MarkupRule>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("MarkupRule_pkey");

            entity.ToTable("MarkupRule");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Active)
                .HasDefaultValue(true)
                .HasColumnName("active");
            entity.Property(e => e.ClientCategoryId).HasColumnName("clientCategoryId");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Label).HasColumnName("label");
            entity.Property(e => e.ManufacturerName).HasColumnName("manufacturerName");
            entity.Property(e => e.PartNumberPrefix).HasColumnName("partNumberPrefix");
            entity.Property(e => e.Priority).HasColumnName("priority");
            entity.Property(e => e.PurchasePriceFrom).HasColumnName("purchasePriceFrom");
            entity.Property(e => e.PurchasePriceTo).HasColumnName("purchasePriceTo");
            entity.Property(e => e.SupplierId).HasColumnName("supplierId");
            entity.Property(e => e.Type)
                .HasDefaultValueSql("'PERCENT'::text")
                .HasColumnName("type");
            entity.Property(e => e.Value).HasColumnName("value");
            entity.Property(e => e.VehicleSystemSlug).HasColumnName("vehicleSystemSlug");

            entity.HasOne(d => d.ClientCategory).WithMany(p => p.MarkupRules)
                .HasForeignKey(d => d.ClientCategoryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("MarkupRule_clientCategoryId_fkey");

            entity.HasOne(d => d.Supplier).WithMany(p => p.MarkupRules)
                .HasForeignKey(d => d.SupplierId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("MarkupRule_supplierId_fkey");
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Notification_pkey");

            entity.ToTable("Notification");

            entity.HasIndex(e => new { e.ClientId, e.ReadAt }, "Notification_clientId_readAt_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.ClientId).HasColumnName("clientId");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Link).HasColumnName("link");
            entity.Property(e => e.ReadAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("readAt");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.Type)
                .HasDefaultValueSql("'system'::text")
                .HasColumnName("type");

            entity.HasOne(d => d.Client).WithMany(p => p.Notifications)
                .HasForeignKey(d => d.ClientId)
                .HasConstraintName("Notification_clientId_fkey");
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Order_pkey");

            entity.ToTable("Order");

            entity.HasIndex(e => e.Reference, "Order_reference_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ClientId).HasColumnName("clientId");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.CurrencyCode)
                .HasDefaultValueSql("'EUR'::text")
                .HasColumnName("currencyCode");
            entity.Property(e => e.CurrencyRate)
                .HasDefaultValue(1.0)
                .HasColumnName("currencyRate");
            entity.Property(e => e.Reference).HasColumnName("reference");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'order_is_sent'::text")
                .HasColumnName("status");

            entity.HasOne(d => d.Client).WithMany(p => p.Orders)
                .HasForeignKey(d => d.ClientId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Order_clientId_fkey");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("OrderItem_pkey");

            entity.ToTable("OrderItem");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrderId).HasColumnName("orderId");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.Quantity)
                .HasDefaultValue(1)
                .HasColumnName("quantity");
            entity.Property(e => e.UnitPrice).HasColumnName("unitPrice");

            entity.HasOne(d => d.Order).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("OrderItem_orderId_fkey");

            entity.HasOne(d => d.Product).WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("OrderItem_productId_fkey");
        });

        modelBuilder.Entity<OrderItemAllocation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("OrderItemAllocation_pkey");

            entity.ToTable("OrderItemAllocation");

            entity.HasIndex(e => new { e.OrderItemId, e.WarehouseId }, "OrderItemAllocation_orderItemId_warehouseId_key").IsUnique();

            entity.HasIndex(e => e.WarehouseId, "OrderItemAllocation_warehouseId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrderItemId).HasColumnName("orderItemId");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.WarehouseId).HasColumnName("warehouseId");

            entity.HasOne(d => d.OrderItem).WithMany(p => p.OrderItemAllocations)
                .HasForeignKey(d => d.OrderItemId)
                .HasConstraintName("OrderItemAllocation_orderItemId_fkey");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.OrderItemAllocations)
                .HasForeignKey(d => d.WarehouseId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("OrderItemAllocation_warehouseId_fkey");
        });

        modelBuilder.Entity<PriceList>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PriceList_pkey");

            entity.ToTable("PriceList");

            entity.HasIndex(e => e.Active, "PriceList_one_active")
                .IsUnique()
                .HasFilter("(active = true)");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Active).HasColumnName("active");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.SourceName).HasColumnName("sourceName");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");
        });

        modelBuilder.Entity<PriceListItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PriceListItem_pkey");

            entity.ToTable("PriceListItem");

            entity.HasIndex(e => new { e.PriceListId, e.ProductId }, "PriceListItem_priceListId_productId_key").IsUnique();

            entity.HasIndex(e => e.ProductId, "PriceListItem_productId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Price).HasColumnName("price");
            entity.Property(e => e.PriceListId).HasColumnName("priceListId");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.SourceCurrency).HasColumnName("sourceCurrency");
            entity.Property(e => e.SourcePrice).HasColumnName("sourcePrice");

            entity.HasOne(d => d.PriceList).WithMany(p => p.PriceListItems)
                .HasForeignKey(d => d.PriceListId)
                .HasConstraintName("PriceListItem_priceListId_fkey");

            entity.HasOne(d => d.Product).WithMany(p => p.PriceListItems)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("PriceListItem_productId_fkey");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Product_pkey");

            entity.ToTable("Product");

            entity.HasIndex(e => e.PartNumber, "Product_partNumber_key").IsUnique();

            entity.HasIndex(e => e.SupplierId, "Product_supplierId_idx");

            entity.HasIndex(e => e.TecDocId, "Product_tecDocId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.BasePrice).HasColumnName("basePrice");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Currency)
                .HasDefaultValueSql("'EUR'::text")
                .HasColumnName("currency");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.ManufacturerId).HasColumnName("manufacturerId");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.PartNumber).HasColumnName("partNumber");
            entity.Property(e => e.StockDays)
                .HasDefaultValue(1)
                .HasColumnName("stockDays");
            entity.Property(e => e.SupplierId).HasColumnName("supplierId");
            entity.Property(e => e.TecDocId).HasColumnName("tecDocId");
            entity.Property(e => e.VehicleSystemId).HasColumnName("vehicleSystemId");

            entity.HasOne(d => d.Manufacturer).WithMany(p => p.Products)
                .HasForeignKey(d => d.ManufacturerId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Product_manufacturerId_fkey");

            entity.HasOne(d => d.Supplier).WithMany(p => p.Products)
                .HasForeignKey(d => d.SupplierId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Product_supplierId_fkey");

            entity.HasOne(d => d.VehicleSystem).WithMany(p => p.Products)
                .HasForeignKey(d => d.VehicleSystemId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("Product_vehicleSystemId_fkey");
        });

        modelBuilder.Entity<ProductImage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ProductImage_pkey");

            entity.ToTable("ProductImage");

            entity.HasIndex(e => new { e.ProductId, e.SortOrder }, "ProductImage_productId_sortOrder_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Alt).HasColumnName("alt");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.SortOrder).HasColumnName("sortOrder");
            entity.Property(e => e.Url).HasColumnName("url");

            entity.HasOne(d => d.Product).WithMany(p => p.ProductImages)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("ProductImage_productId_fkey");
        });

        modelBuilder.Entity<RetailOutlet>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("RetailOutlet_pkey");

            entity.ToTable("RetailOutlet");

            entity.HasIndex(e => e.Code, "RetailOutlet_code_key").IsUnique();

            entity.HasIndex(e => e.WarehouseId, "RetailOutlet_warehouseId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Active)
                .HasDefaultValue(true)
                .HasColumnName("active");
            entity.Property(e => e.Address).HasColumnName("address");
            entity.Property(e => e.City).HasColumnName("city");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.WarehouseId).HasColumnName("warehouseId");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.RetailOutlets)
                .HasForeignKey(d => d.WarehouseId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("RetailOutlet_warehouseId_fkey");
        });

        modelBuilder.Entity<StockLevel>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("StockLevel_pkey");

            entity.ToTable("StockLevel");

            entity.HasIndex(e => new { e.ProductId, e.WarehouseId }, "StockLevel_productId_warehouseId_key").IsUnique();

            entity.HasIndex(e => e.WarehouseId, "StockLevel_warehouseId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.BinLocation).HasColumnName("binLocation");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.Reserved).HasColumnName("reserved");
            entity.Property(e => e.UpdatedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("updatedAt");
            entity.Property(e => e.WarehouseId).HasColumnName("warehouseId");

            entity.HasOne(d => d.Product).WithMany(p => p.StockLevels)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("StockLevel_productId_fkey");

            entity.HasOne(d => d.Warehouse).WithMany(p => p.StockLevels)
                .HasForeignKey(d => d.WarehouseId)
                .HasConstraintName("StockLevel_warehouseId_fkey");
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Supplier_pkey");

            entity.ToTable("Supplier");

            entity.HasIndex(e => e.AcceptsReturns, "Supplier_acceptsReturns_idx");

            entity.HasIndex(e => e.Code, "Supplier_code_key").IsUnique();

            entity.HasIndex(e => e.Country, "Supplier_country_idx");

            entity.HasIndex(e => e.PurchaseCurrencyId, "Supplier_purchaseCurrencyId_idx");

            entity.HasIndex(e => e.Rating, "Supplier_rating_idx");

            entity.HasIndex(e => e.Slug, "Supplier_slug_key").IsUnique();

            entity.HasIndex(e => e.Active, "Supplier_waiting_idx").HasFilter("(active = false)");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.AcceptsReturns).HasColumnName("acceptsReturns");
            entity.Property(e => e.Active)
                .HasDefaultValue(true)
                .HasColumnName("active");
            entity.Property(e => e.ApprovedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("approvedAt");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.Country).HasColumnName("country");
            entity.Property(e => e.DefaultStockDays).HasColumnName("defaultStockDays");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.GuaranteeMonths).HasColumnName("guaranteeMonths");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.PurchaseCurrencyId).HasColumnName("purchaseCurrencyId");
            entity.Property(e => e.Rating).HasColumnName("rating");
            entity.Property(e => e.Reliability)
                .HasDefaultValueSql("'standard'::text")
                .HasColumnName("reliability");
            entity.Property(e => e.Slug).HasColumnName("slug");

            entity.HasOne(d => d.PurchaseCurrency).WithMany(p => p.Suppliers)
                .HasForeignKey(d => d.PurchaseCurrencyId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Supplier_purchaseCurrencyId_fkey");
        });

        modelBuilder.Entity<VehicleMake>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("VehicleMake_pkey");

            entity.ToTable("VehicleMake");

            entity.HasIndex(e => e.Name, "VehicleMake_name_key").IsUnique();

            entity.HasIndex(e => e.TecDocId, "VehicleMake_tecDocId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.TecDocId).HasColumnName("tecDocId");
            entity.Property(e => e.WmiCodes).HasColumnName("wmiCodes");
        });

        modelBuilder.Entity<VehicleModel>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("VehicleModel_pkey");

            entity.ToTable("VehicleModel");

            entity.HasIndex(e => new { e.MakeId, e.Name }, "VehicleModel_makeId_name_key").IsUnique();

            entity.HasIndex(e => e.TecDocId, "VehicleModel_tecDocId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.MakeId).HasColumnName("makeId");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.TecDocId).HasColumnName("tecDocId");
            entity.Property(e => e.YearFrom).HasColumnName("yearFrom");
            entity.Property(e => e.YearTo).HasColumnName("yearTo");

            entity.HasOne(d => d.Make).WithMany(p => p.VehicleModels)
                .HasForeignKey(d => d.MakeId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("VehicleModel_makeId_fkey");
        });

        modelBuilder.Entity<VehicleSystem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("VehicleSystem_pkey");

            entity.ToTable("VehicleSystem");

            entity.HasIndex(e => e.Slug, "VehicleSystem_slug_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Icon).HasColumnName("icon");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Order).HasColumnName("order");
            entity.Property(e => e.Slug).HasColumnName("slug");
        });

        modelBuilder.Entity<VehicleVariant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("VehicleVariant_pkey");

            entity.ToTable("VehicleVariant");

            entity.HasIndex(e => new { e.ModelId, e.Name }, "VehicleVariant_modelId_name_key").IsUnique();

            entity.HasIndex(e => e.TecDocId, "VehicleVariant_tecDocId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EngineCode).HasColumnName("engineCode");
            entity.Property(e => e.Fuel)
                .HasDefaultValueSql("'diesel'::text")
                .HasColumnName("fuel");
            entity.Property(e => e.ModelId).HasColumnName("modelId");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.PowerKw).HasColumnName("powerKw");
            entity.Property(e => e.TecDocId).HasColumnName("tecDocId");
            entity.Property(e => e.YearFrom).HasColumnName("yearFrom");
            entity.Property(e => e.YearTo).HasColumnName("yearTo");

            entity.HasOne(d => d.Model).WithMany(p => p.VehicleVariants)
                .HasForeignKey(d => d.ModelId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("VehicleVariant_modelId_fkey");
        });

        modelBuilder.Entity<VerificationToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("VerificationToken_pkey");

            entity.ToTable("VerificationToken");

            entity.HasIndex(e => new { e.ClientId, e.Purpose }, "VerificationToken_clientId_purpose_idx");

            entity.HasIndex(e => e.TokenHash, "VerificationToken_tokenHash_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ClientId).HasColumnName("clientId");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.ExpiresAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("expiresAt");
            entity.Property(e => e.Purpose).HasColumnName("purpose");
            entity.Property(e => e.TokenHash).HasColumnName("tokenHash");
            entity.Property(e => e.UsedAt)
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("usedAt");

            entity.HasOne(d => d.Client).WithMany(p => p.VerificationTokens)
                .HasForeignKey(d => d.ClientId)
                .HasConstraintName("VerificationToken_clientId_fkey");
        });

        modelBuilder.Entity<Warehouse>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Warehouse_pkey");

            entity.ToTable("Warehouse");

            entity.HasIndex(e => e.Code, "Warehouse_code_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Active)
                .HasDefaultValue(true)
                .HasColumnName("active");
            entity.Property(e => e.Address).HasColumnName("address");
            entity.Property(e => e.City).HasColumnName("city");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp(3) without time zone")
                .HasColumnName("createdAt");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Priority).HasColumnName("priority");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
