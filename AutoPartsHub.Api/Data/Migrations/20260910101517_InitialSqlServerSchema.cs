using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientCategory",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    markupPercent = table.Column<double>(type: "float", nullable: false),
                    minOrderAmount = table.Column<double>(type: "float", nullable: false),
                    requiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    shelfLifeDays = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("ClientCategory_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Currency",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    symbol = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    rate = table.Column<double>(type: "float", nullable: false, defaultValue: 1.0),
                    isBase = table.Column<bool>(type: "bit", nullable: false),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Currency_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Manufacturer",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    isOEM = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Manufacturer_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "MarkupRule",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    priority = table.Column<int>(type: "int", nullable: false),
                    specificity = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    purchasePriceFrom = table.Column<double>(type: "float", nullable: true),
                    purchasePriceTo = table.Column<double>(type: "float", nullable: true),
                    type = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'PERCENT'"),
                    value = table.Column<double>(type: "float", nullable: false),
                    minAmount = table.Column<double>(type: "float", nullable: true),
                    startsAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    endsAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("MarkupRule_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "PriceList",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false),
                    sourceName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PriceList_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleMake",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    wmiCodes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    tecDocId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("VehicleMake_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleSystem",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    icon = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("VehicleSystem_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Warehouse",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    city = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    priority = table.Column<int>(type: "int", nullable: false),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("Warehouse_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Supplier",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    reliability = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'standard'"),
                    slug = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    rating = table.Column<int>(type: "int", nullable: true),
                    acceptsReturns = table.Column<bool>(type: "bit", nullable: true),
                    country = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    guaranteeMonths = table.Column<int>(type: "int", nullable: true),
                    defaultStockDays = table.Column<int>(type: "int", nullable: true),
                    purchaseCurrencyId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    approvedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Supplier_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Supplier_purchaseCurrencyId_fkey",
                        column: x => x.purchaseCurrencyId,
                        principalTable: "Currency",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MarkupRuleCondition",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ruleId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    dimension = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    negated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("MarkupRuleCondition_pkey", x => x.id);
                    table.ForeignKey(
                        name: "MarkupRuleCondition_ruleId_fkey",
                        column: x => x.ruleId,
                        principalTable: "MarkupRule",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleModel",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    makeId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    yearFrom = table.Column<int>(type: "int", nullable: false),
                    yearTo = table.Column<int>(type: "int", nullable: true),
                    tecDocId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("VehicleModel_pkey", x => x.id);
                    table.ForeignKey(
                        name: "VehicleModel_makeId_fkey",
                        column: x => x.makeId,
                        principalTable: "VehicleMake",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetailOutlet",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    city = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    phone = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    warehouseId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("RetailOutlet_pkey", x => x.id);
                    table.ForeignKey(
                        name: "RetailOutlet_warehouseId_fkey",
                        column: x => x.warehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Client",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    email = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    passwordHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    role = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'RETAIL'"),
                    city = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    categoryId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    discountPercent = table.Column<double>(type: "float", nullable: false),
                    currencyId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    salesManagerId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    supplierId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    emailConfirmedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Client_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Client_categoryId_fkey",
                        column: x => x.categoryId,
                        principalTable: "ClientCategory",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "Client_currencyId_fkey",
                        column: x => x.currencyId,
                        principalTable: "Currency",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "Client_salesManagerId_fkey",
                        column: x => x.salesManagerId,
                        principalTable: "Client",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "Client_supplierId_fkey",
                        column: x => x.supplierId,
                        principalTable: "Supplier",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Product",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    partNumber = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    manufacturerId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    vehicleSystemId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    basePrice = table.Column<double>(type: "float", nullable: false),
                    currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'EUR'"),
                    stockDays = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    supplierId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    tecDocId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Product_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Product_manufacturerId_fkey",
                        column: x => x.manufacturerId,
                        principalTable: "Manufacturer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "Product_supplierId_fkey",
                        column: x => x.supplierId,
                        principalTable: "Supplier",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "Product_vehicleSystemId_fkey",
                        column: x => x.vehicleSystemId,
                        principalTable: "VehicleSystem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleVariant",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    modelId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    engineCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    powerKw = table.Column<int>(type: "int", nullable: true),
                    fuel = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'diesel'"),
                    yearFrom = table.Column<int>(type: "int", nullable: false),
                    yearTo = table.Column<int>(type: "int", nullable: true),
                    tecDocId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("VehicleVariant_pkey", x => x.id);
                    table.ForeignKey(
                        name: "VehicleVariant_modelId_fkey",
                        column: x => x.modelId,
                        principalTable: "VehicleModel",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cart",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Cart_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Cart_clientId_fkey",
                        column: x => x.clientId,
                        principalTable: "Client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    type = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'system'"),
                    title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    link = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    readAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("Notification_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Notification_clientId_fkey",
                        column: x => x.clientId,
                        principalTable: "Client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Order",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    reference = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    status = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'order_is_sent'"),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    currencyCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'EUR'"),
                    currencyRate = table.Column<double>(type: "float", nullable: false, defaultValue: 1.0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Order_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Order_clientId_fkey",
                        column: x => x.clientId,
                        principalTable: "Client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VerificationToken",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    purpose = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    tokenHash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    expiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    usedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("VerificationToken_pkey", x => x.id);
                    table.ForeignKey(
                        name: "VerificationToken_clientId_fkey",
                        column: x => x.clientId,
                        principalTable: "Client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Interchange",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    sourceId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    targetPartNo = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    targetManufacturer = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    exactMatch = table.Column<bool>(type: "bit", nullable: false),
                    isOEM = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Interchange_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Interchange_sourceId_fkey",
                        column: x => x.sourceId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceListItem",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    priceListId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    price = table.Column<double>(type: "float", nullable: false),
                    sourcePrice = table.Column<double>(type: "float", nullable: true),
                    sourceCurrency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PriceListItem_pkey", x => x.id);
                    table.ForeignKey(
                        name: "PriceListItem_priceListId_fkey",
                        column: x => x.priceListId,
                        principalTable: "PriceList",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "PriceListItem_productId_fkey",
                        column: x => x.productId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductImage",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    alt = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    sortOrder = table.Column<int>(type: "int", nullable: false),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("ProductImage_pkey", x => x.id);
                    table.ForeignKey(
                        name: "ProductImage_productId_fkey",
                        column: x => x.productId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockLevel",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    warehouseId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false),
                    reserved = table.Column<int>(type: "int", nullable: false),
                    binLocation = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    updatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("StockLevel_pkey", x => x.id);
                    table.ForeignKey(
                        name: "StockLevel_productId_fkey",
                        column: x => x.productId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "StockLevel_warehouseId_fkey",
                        column: x => x.warehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Fitment",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    variantId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    note = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Fitment_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Fitment_productId_fkey",
                        column: x => x.productId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "Fitment_variantId_fkey",
                        column: x => x.variantId,
                        principalTable: "VehicleVariant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CartItem",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    cartId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    addedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("CartItem_pkey", x => x.id);
                    table.ForeignKey(
                        name: "CartItem_cartId_fkey",
                        column: x => x.cartId,
                        principalTable: "Cart",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "CartItem_productId_fkey",
                        column: x => x.productId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderItem",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    orderId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    unitPrice = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("OrderItem_pkey", x => x.id);
                    table.ForeignKey(
                        name: "OrderItem_orderId_fkey",
                        column: x => x.orderId,
                        principalTable: "Order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "OrderItem_productId_fkey",
                        column: x => x.productId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderItemAllocation",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    orderItemId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    warehouseId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("OrderItemAllocation_pkey", x => x.id);
                    table.ForeignKey(
                        name: "OrderItemAllocation_orderItemId_fkey",
                        column: x => x.orderItemId,
                        principalTable: "OrderItem",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "OrderItemAllocation_warehouseId_fkey",
                        column: x => x.warehouseId,
                        principalTable: "Warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "Cart_clientId_key",
                table: "Cart",
                column: "clientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "CartItem_cartId_productId_key",
                table: "CartItem",
                columns: new[] { "cartId", "productId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "CartItem_productId_idx",
                table: "CartItem",
                column: "productId");

            migrationBuilder.CreateIndex(
                name: "Client_currencyId_idx",
                table: "Client",
                column: "currencyId");

            migrationBuilder.CreateIndex(
                name: "Client_email_key",
                table: "Client",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Client_salesManagerId_idx",
                table: "Client",
                column: "salesManagerId");

            migrationBuilder.CreateIndex(
                name: "Client_supplierId_idx",
                table: "Client",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Client_categoryId",
                table: "Client",
                column: "categoryId");

            migrationBuilder.CreateIndex(
                name: "Currency_code_key",
                table: "Currency",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Currency_single_base",
                table: "Currency",
                column: "isBase",
                unique: true,
                filter: "([isBase] = 1)");

            migrationBuilder.CreateIndex(
                name: "Fitment_productId_variantId_key",
                table: "Fitment",
                columns: new[] { "productId", "variantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Fitment_variantId_idx",
                table: "Fitment",
                column: "variantId");

            migrationBuilder.CreateIndex(
                name: "Interchange_isOEM_idx",
                table: "Interchange",
                column: "isOEM");

            migrationBuilder.CreateIndex(
                name: "IX_Interchange_sourceId",
                table: "Interchange",
                column: "sourceId");

            migrationBuilder.CreateIndex(
                name: "Manufacturer_name_key",
                table: "Manufacturer",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "MarkupRuleCondition_ruleId_dimension_value_key",
                table: "MarkupRuleCondition",
                columns: new[] { "ruleId", "dimension", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Notification_clientId_readAt_idx",
                table: "Notification",
                columns: new[] { "clientId", "readAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Order_clientId",
                table: "Order",
                column: "clientId");

            migrationBuilder.CreateIndex(
                name: "Order_reference_key",
                table: "Order",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItem_orderId",
                table: "OrderItem",
                column: "orderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItem_productId",
                table: "OrderItem",
                column: "productId");

            migrationBuilder.CreateIndex(
                name: "OrderItemAllocation_orderItemId_warehouseId_key",
                table: "OrderItemAllocation",
                columns: new[] { "orderItemId", "warehouseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "OrderItemAllocation_warehouseId_idx",
                table: "OrderItemAllocation",
                column: "warehouseId");

            migrationBuilder.CreateIndex(
                name: "PriceList_one_active",
                table: "PriceList",
                column: "active",
                unique: true,
                filter: "([active] = 1)");

            migrationBuilder.CreateIndex(
                name: "PriceListItem_priceListId_productId_key",
                table: "PriceListItem",
                columns: new[] { "priceListId", "productId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "PriceListItem_productId_idx",
                table: "PriceListItem",
                column: "productId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_manufacturerId",
                table: "Product",
                column: "manufacturerId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_vehicleSystemId",
                table: "Product",
                column: "vehicleSystemId");

            migrationBuilder.CreateIndex(
                name: "Product_partNumber_key",
                table: "Product",
                column: "partNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Product_supplierId_idx",
                table: "Product",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "Product_tecDocId_key",
                table: "Product",
                column: "tecDocId",
                unique: true,
                filter: "[tecDocId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ProductImage_productId_sortOrder_idx",
                table: "ProductImage",
                columns: new[] { "productId", "sortOrder" });

            migrationBuilder.CreateIndex(
                name: "RetailOutlet_code_key",
                table: "RetailOutlet",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "RetailOutlet_warehouseId_idx",
                table: "RetailOutlet",
                column: "warehouseId");

            migrationBuilder.CreateIndex(
                name: "StockLevel_productId_warehouseId_key",
                table: "StockLevel",
                columns: new[] { "productId", "warehouseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "StockLevel_warehouseId_idx",
                table: "StockLevel",
                column: "warehouseId");

            migrationBuilder.CreateIndex(
                name: "Supplier_acceptsReturns_idx",
                table: "Supplier",
                column: "acceptsReturns");

            migrationBuilder.CreateIndex(
                name: "Supplier_code_key",
                table: "Supplier",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Supplier_country_idx",
                table: "Supplier",
                column: "country");

            migrationBuilder.CreateIndex(
                name: "Supplier_purchaseCurrencyId_idx",
                table: "Supplier",
                column: "purchaseCurrencyId");

            migrationBuilder.CreateIndex(
                name: "Supplier_rating_idx",
                table: "Supplier",
                column: "rating");

            migrationBuilder.CreateIndex(
                name: "Supplier_slug_key",
                table: "Supplier",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Supplier_waiting_idx",
                table: "Supplier",
                column: "active",
                filter: "([active] = 0)");

            migrationBuilder.CreateIndex(
                name: "VehicleMake_name_key",
                table: "VehicleMake",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "VehicleMake_tecDocId_key",
                table: "VehicleMake",
                column: "tecDocId",
                unique: true,
                filter: "[tecDocId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "VehicleModel_makeId_name_key",
                table: "VehicleModel",
                columns: new[] { "makeId", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "VehicleModel_tecDocId_key",
                table: "VehicleModel",
                column: "tecDocId",
                unique: true,
                filter: "[tecDocId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "VehicleSystem_slug_key",
                table: "VehicleSystem",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "VehicleVariant_modelId_name_key",
                table: "VehicleVariant",
                columns: new[] { "modelId", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "VehicleVariant_tecDocId_key",
                table: "VehicleVariant",
                column: "tecDocId",
                unique: true,
                filter: "[tecDocId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "VerificationToken_clientId_purpose_idx",
                table: "VerificationToken",
                columns: new[] { "clientId", "purpose" });

            migrationBuilder.CreateIndex(
                name: "VerificationToken_tokenHash_key",
                table: "VerificationToken",
                column: "tokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Warehouse_code_key",
                table: "Warehouse",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CartItem");

            migrationBuilder.DropTable(
                name: "Fitment");

            migrationBuilder.DropTable(
                name: "Interchange");

            migrationBuilder.DropTable(
                name: "MarkupRuleCondition");

            migrationBuilder.DropTable(
                name: "Notification");

            migrationBuilder.DropTable(
                name: "OrderItemAllocation");

            migrationBuilder.DropTable(
                name: "PriceListItem");

            migrationBuilder.DropTable(
                name: "ProductImage");

            migrationBuilder.DropTable(
                name: "RetailOutlet");

            migrationBuilder.DropTable(
                name: "StockLevel");

            migrationBuilder.DropTable(
                name: "VerificationToken");

            migrationBuilder.DropTable(
                name: "Cart");

            migrationBuilder.DropTable(
                name: "VehicleVariant");

            migrationBuilder.DropTable(
                name: "MarkupRule");

            migrationBuilder.DropTable(
                name: "OrderItem");

            migrationBuilder.DropTable(
                name: "PriceList");

            migrationBuilder.DropTable(
                name: "Warehouse");

            migrationBuilder.DropTable(
                name: "VehicleModel");

            migrationBuilder.DropTable(
                name: "Order");

            migrationBuilder.DropTable(
                name: "Product");

            migrationBuilder.DropTable(
                name: "VehicleMake");

            migrationBuilder.DropTable(
                name: "Client");

            migrationBuilder.DropTable(
                name: "Manufacturer");

            migrationBuilder.DropTable(
                name: "VehicleSystem");

            migrationBuilder.DropTable(
                name: "ClientCategory");

            migrationBuilder.DropTable(
                name: "Supplier");

            migrationBuilder.DropTable(
                name: "Currency");
        }
    }
}
