using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <summary>
    /// The two tables the scope enum has been waiting for.
    /// </summary>
    /// <remarks>
    /// ManagerAccess says how far one salesperson's reach goes; ExtraClient is
    /// the list it consults when that reach is "selected". Every guard and
    /// every scoped query in this application already handles all three
    /// degrees — only Own was reachable, because there was nowhere to record
    /// the other two.
    ///
    /// The PostgreSQL half is its own migration in the storefront repository
    /// (20260912120000_manager_access), because that schema is Prisma's. The
    /// two are written to agree, and tools/schema-audit.mjs is what says
    /// whether they do.
    ///
    /// NOTHING IS GRANTED BY THIS. No rows are inserted, so every salesperson
    /// keeps exactly the reach they have today: an absent row reads as Own.
    /// </remarks>
    public partial class ManagerReach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExtraClient",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    managerId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    grantedById = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("ExtraClient_pkey", x => x.id);
                    table.CheckConstraint("ExtraClient_not_self", "\"managerId\" <> \"clientId\"");
                    table.ForeignKey(
                        name: "ExtraClient_clientId_fkey",
                        column: x => x.clientId,
                        principalTable: "Client",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "ExtraClient_grantedById_fkey",
                        column: x => x.grantedById,
                        principalTable: "Client",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "ExtraClient_managerId_fkey",
                        column: x => x.managerId,
                        principalTable: "Client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManagerAccess",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    managerId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    reach = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, defaultValue: "own"),
                    grantedById = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("ManagerAccess_pkey", x => x.id);
                    table.CheckConstraint("ManagerAccess_reach_known", "\"reach\" IN ('own', 'selected', 'all')");
                    table.ForeignKey(
                        name: "ManagerAccess_grantedById_fkey",
                        column: x => x.grantedById,
                        principalTable: "Client",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "ManagerAccess_managerId_fkey",
                        column: x => x.managerId,
                        principalTable: "Client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ExtraClient_clientId_idx",
                table: "ExtraClient",
                column: "clientId");

            migrationBuilder.CreateIndex(
                name: "ExtraClient_managerId_clientId_key",
                table: "ExtraClient",
                columns: new[] { "managerId", "clientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ExtraClient_managerId_idx",
                table: "ExtraClient",
                column: "managerId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtraClient_grantedById",
                table: "ExtraClient",
                column: "grantedById");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerAccess_grantedById",
                table: "ManagerAccess",
                column: "grantedById");

            migrationBuilder.CreateIndex(
                name: "ManagerAccess_managerId_key",
                table: "ManagerAccess",
                column: "managerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtraClient");

            migrationBuilder.DropTable(
                name: "ManagerAccess");
        }
    }
}
