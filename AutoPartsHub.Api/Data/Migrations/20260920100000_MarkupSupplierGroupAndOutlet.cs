using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MarkupSupplierGroupAndOutlet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "groupName",
                table: "Supplier",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outletId",
                table: "Client",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "Client_outletId_idx",
                table: "Client",
                column: "outletId");

            migrationBuilder.AddForeignKey(
                name: "Client_outletId_fkey",
                table: "Client",
                column: "outletId",
                principalTable: "RetailOutlet",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "Client_outletId_fkey",
                table: "Client");

            migrationBuilder.DropIndex(
                name: "Client_outletId_idx",
                table: "Client");

            migrationBuilder.DropColumn(
                name: "groupName",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "outletId",
                table: "Client");
        }
    }
}
