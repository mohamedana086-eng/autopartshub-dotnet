using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketFollowers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketFollower",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ticketId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    createdAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("TicketFollower_pkey", x => x.id);
                    table.ForeignKey(
                        name: "TicketFollower_clientId_fkey",
                        column: x => x.clientId,
                        principalTable: "Client",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "TicketFollower_ticketId_fkey",
                        column: x => x.ticketId,
                        principalTable: "Ticket",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "TicketFollower_clientId_idx",
                table: "TicketFollower",
                column: "clientId");

            migrationBuilder.CreateIndex(
                name: "TicketFollower_ticketId_clientId_key",
                table: "TicketFollower",
                columns: new[] { "ticketId", "clientId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketFollower");
        }
    }
}
