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
                name: "GoodsCategory",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    markupType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    markupValue = table.Column<double>(type: "float", nullable: true),
                    markupMinAmount = table.Column<double>(type: "float", nullable: true),
                    sortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("GoodsCategory_pkey", x => x.id);
                    table.CheckConstraint("GoodsCategory_floor_matches_type", "(\"markupMinAmount\" IS NULL\n AND (\"markupType\" IS NULL OR \"markupType\" <> 'PERCENT_MIN'))\nOR (\"markupMinAmount\" IS NOT NULL AND \"markupType\" = 'PERCENT_MIN')");
                    table.CheckConstraint("GoodsCategory_floor_positive", "\"markupMinAmount\" IS NULL OR \"markupMinAmount\" >= 0");
                    table.CheckConstraint("GoodsCategory_markup_complete", "(\"markupType\" IS NULL AND \"markupValue\" IS NULL)\nOR (\"markupType\" IS NOT NULL AND \"markupValue\" IS NOT NULL)");
                    table.CheckConstraint("GoodsCategory_markupType_known", "\"markupType\" IS NULL\nOR \"markupType\" IN ('PERCENT', 'AMOUNT', 'FIXED', 'PERCENT_MIN')");
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    goodsCategoryId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    markupPercent = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PriceList_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "SearchMiss",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    term = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    narrowed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    searches = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    firstSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    lastSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("SearchMiss_pkey", x => x.id);
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
                name: "VinLookup",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    pattern = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    wmi = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    modelYear = table.Column<int>(type: "int", nullable: true),
                    makeName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    candidateCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    lookups = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    firstSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    lastSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    decodedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    decoded = table.Column<bool>(type: "bit", nullable: true),
                    decodedModel = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    decodedTrim = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    decodedEngine = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    decoderStatus = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("VinLookup_pkey", x => x.id);
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
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
                    approvedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    markupPercent = table.Column<double>(type: "float", nullable: true),
                    minOrderAmount = table.Column<double>(type: "float", nullable: false, defaultValue: 0.0),
                    priority = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
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
                name: "PriceListImport",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    priceListId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    listName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    sourceName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    uploadedById = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    uploadedByName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    outcome = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    rowsSent = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    accepted = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    rejected = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    rejectedStored = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PriceListImport_pkey", x => x.id);
                    table.ForeignKey(
                        name: "PriceListImport_priceListId_fkey",
                        column: x => x.priceListId,
                        principalTable: "PriceList",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
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
                    tecDocId = table.Column<int>(type: "int", nullable: true),
                    series = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    supplierId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    tecDocId = table.Column<int>(type: "int", nullable: true),
                    goodsCategoryId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    packagingUnit = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValue: "piece"),
                    partType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValue: "aftermarket"),
                    quantityPerPackage = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    weightGrams = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("Product_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Product_goodsCategoryId_fkey",
                        column: x => x.goodsCategoryId,
                        principalTable: "GoodsCategory",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
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
                name: "PriceListImportRow",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    importId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    line = table.Column<int>(type: "int", nullable: false),
                    partNumber = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    price = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    currency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PriceListImportRow_pkey", x => x.id);
                    table.ForeignKey(
                        name: "PriceListImportRow_importId_fkey",
                        column: x => x.importId,
                        principalTable: "PriceListImport",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
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
                    tecDocId = table.Column<int>(type: "int", nullable: true),
                    bodyType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    region = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    steeringSide = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    transmission = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    currencyCode = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValueSql: "'EUR'"),
                    currencyRate = table.Column<double>(type: "float", nullable: false, defaultValue: 1.0),
                    carrier = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    statusChangedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    statusChangedById = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    statusReason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    trackingNumber = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    weightComplete = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    weightGrams = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
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
                    sourceCurrency = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    markupPercent = table.Column<double>(type: "float", nullable: true),
                    sourcePartNumber = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
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
                name: "ProductBarcode",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    code = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    kind = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValue: "other"),
                    sortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("ProductBarcode_pkey", x => x.id);
                    table.ForeignKey(
                        name: "ProductBarcode_productId_fkey",
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
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
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
                name: "ProductSpec",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    label = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    unit = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    sortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("ProductSpec_pkey", x => x.id);
                    table.ForeignKey(
                        name: "ProductSpec_productId_fkey",
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
                name: "SupplierOffer",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    productId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    supplierId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    purchasePrice = table.Column<double>(type: "float", nullable: false),
                    stockDays = table.Column<int>(type: "int", nullable: true),
                    supplierPartNumber = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("SupplierOffer_pkey", x => x.id);
                    table.ForeignKey(
                        name: "SupplierOffer_productId_fkey",
                        column: x => x.productId,
                        principalTable: "Product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "SupplierOffer_supplierId_fkey",
                        column: x => x.supplierId,
                        principalTable: "Supplier",
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
                    addedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
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
                name: "Ticket",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    reference = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    orderId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    status = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValue: "open"),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    lastMessageAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("Ticket_pkey", x => x.id);
                    table.ForeignKey(
                        name: "Ticket_clientId_fkey",
                        column: x => x.clientId,
                        principalTable: "Client",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "Ticket_orderId_fkey",
                        column: x => x.orderId,
                        principalTable: "Order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
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

            migrationBuilder.CreateTable(
                name: "TicketMessage",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ticketId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    authorId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    authorName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    fromStaff = table.Column<bool>(type: "bit", nullable: false),
                    @internal = table.Column<bool>(name: "internal", type: "bit", nullable: false, defaultValue: false),
                    body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("TicketMessage_pkey", x => x.id);
                    table.ForeignKey(
                        name: "TicketMessage_ticketId_fkey",
                        column: x => x.ticketId,
                        principalTable: "Ticket",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "GoodsCategory_slug_key",
                table: "GoodsCategory",
                column: "slug",
                unique: true);

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
                name: "PriceListImport_createdAt_idx",
                table: "PriceListImport",
                column: "createdAt");

            migrationBuilder.CreateIndex(
                name: "PriceListImport_priceListId_idx",
                table: "PriceListImport",
                column: "priceListId");

            migrationBuilder.CreateIndex(
                name: "PriceListImportRow_importId_line_idx",
                table: "PriceListImportRow",
                columns: new[] { "importId", "line" });

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
                name: "Product_goodsCategoryId_idx",
                table: "Product",
                column: "goodsCategoryId");

            migrationBuilder.CreateIndex(
                name: "Product_partNumber_key",
                table: "Product",
                column: "partNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Product_partType_idx",
                table: "Product",
                column: "partType");

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
                name: "ProductBarcode_code_key",
                table: "ProductBarcode",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ProductBarcode_productId_sortOrder_idx",
                table: "ProductBarcode",
                columns: new[] { "productId", "sortOrder" });

            migrationBuilder.CreateIndex(
                name: "ProductImage_productId_sortOrder_idx",
                table: "ProductImage",
                columns: new[] { "productId", "sortOrder" });

            migrationBuilder.CreateIndex(
                name: "ProductSpec_productId_sortOrder_idx",
                table: "ProductSpec",
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
                name: "SearchMiss_lastSeenAt_idx",
                table: "SearchMiss",
                column: "lastSeenAt");

            migrationBuilder.CreateIndex(
                name: "SearchMiss_term_narrowed_key",
                table: "SearchMiss",
                columns: new[] { "term", "narrowed" },
                unique: true);

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
                name: "SupplierOffer_productId_supplierId_key",
                table: "SupplierOffer",
                columns: new[] { "productId", "supplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "SupplierOffer_supplierId_idx",
                table: "SupplierOffer",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Ticket_orderId",
                table: "Ticket",
                column: "orderId");

            migrationBuilder.CreateIndex(
                name: "Ticket_clientId_idx",
                table: "Ticket",
                column: "clientId");

            migrationBuilder.CreateIndex(
                name: "Ticket_reference_key",
                table: "Ticket",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Ticket_status_lastMessageAt_idx",
                table: "Ticket",
                columns: new[] { "status", "lastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "TicketMessage_ticketId_createdAt_idx",
                table: "TicketMessage",
                columns: new[] { "ticketId", "createdAt" });

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
                name: "VinLookup_pattern_key",
                table: "VinLookup",
                column: "pattern",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "VinLookup_wmi_idx",
                table: "VinLookup",
                column: "wmi");

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
                name: "PriceListImportRow");

            migrationBuilder.DropTable(
                name: "PriceListItem");

            migrationBuilder.DropTable(
                name: "ProductBarcode");

            migrationBuilder.DropTable(
                name: "ProductImage");

            migrationBuilder.DropTable(
                name: "ProductSpec");

            migrationBuilder.DropTable(
                name: "RetailOutlet");

            migrationBuilder.DropTable(
                name: "SearchMiss");

            migrationBuilder.DropTable(
                name: "StockLevel");

            migrationBuilder.DropTable(
                name: "SupplierOffer");

            migrationBuilder.DropTable(
                name: "TicketMessage");

            migrationBuilder.DropTable(
                name: "VerificationToken");

            migrationBuilder.DropTable(
                name: "VinLookup");

            migrationBuilder.DropTable(
                name: "Cart");

            migrationBuilder.DropTable(
                name: "VehicleVariant");

            migrationBuilder.DropTable(
                name: "MarkupRule");

            migrationBuilder.DropTable(
                name: "OrderItem");

            migrationBuilder.DropTable(
                name: "PriceListImport");

            migrationBuilder.DropTable(
                name: "Warehouse");

            migrationBuilder.DropTable(
                name: "Ticket");

            migrationBuilder.DropTable(
                name: "VehicleModel");

            migrationBuilder.DropTable(
                name: "Product");

            migrationBuilder.DropTable(
                name: "PriceList");

            migrationBuilder.DropTable(
                name: "Order");

            migrationBuilder.DropTable(
                name: "VehicleMake");

            migrationBuilder.DropTable(
                name: "GoodsCategory");

            migrationBuilder.DropTable(
                name: "Manufacturer");

            migrationBuilder.DropTable(
                name: "VehicleSystem");

            migrationBuilder.DropTable(
                name: "Client");

            migrationBuilder.DropTable(
                name: "ClientCategory");

            migrationBuilder.DropTable(
                name: "Supplier");

            migrationBuilder.DropTable(
                name: "Currency");
        }
    }
}
