using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FailedSearchResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "resolvedAt",
                table: "SearchMiss",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolvedById",
                table: "SearchMiss",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchMiss_resolvedById",
                table: "SearchMiss",
                column: "resolvedById");

            migrationBuilder.CreateIndex(
                name: "SearchMiss_open_idx",
                table: "SearchMiss",
                column: "searches",
                descending: new bool[0],
                filter: "\"resolvedAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "SearchMiss_resolvedById_fkey",
                table: "SearchMiss",
                column: "resolvedById",
                principalTable: "Client",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "SearchMiss_resolvedById_fkey",
                table: "SearchMiss");

            migrationBuilder.DropIndex(
                name: "IX_SearchMiss_resolvedById",
                table: "SearchMiss");

            migrationBuilder.DropIndex(
                name: "SearchMiss_open_idx",
                table: "SearchMiss");

            migrationBuilder.DropColumn(
                name: "resolvedAt",
                table: "SearchMiss");

            migrationBuilder.DropColumn(
                name: "resolvedById",
                table: "SearchMiss");
        }
    }
}
