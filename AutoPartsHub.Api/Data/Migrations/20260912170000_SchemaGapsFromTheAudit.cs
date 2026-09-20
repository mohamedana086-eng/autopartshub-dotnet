using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <summary>
    /// One unique constraint, three foreign keys and three indexes that
    /// PostgreSQL has and this schema did not.
    /// </summary>
    /// <remarks>
    /// Found by tools/schema-audit.mjs, which exists because the fourteen
    /// missing defaults before it were one class of the same fault and
    /// "fourteen of one kind" is a reason to look for the others.
    ///
    /// What each was costing:
    ///
    /// - GoodsCategory.name had no unique. Two categories of the same name is
    ///   not a crash — it is an admin list with one row in it twice and a
    ///   markup that depends on which of them a part was filed under.
    /// - Three columns naming a Client carried no foreign key, so a row could
    ///   name an account that had never existed.
    /// - Three indexes the storefront's own reports read by.
    ///
    /// The three foreign keys are SET NULL, which is what PostgreSQL has.
    /// They were written NO ACTION first, on the belief that SQL Server would
    /// refuse a second path between two tables it already joins. It refuses a
    /// second CASCADE path, and the first path in each of these is Restrict —
    /// so the belief was wrong, and the engine accepted SET NULL when it was
    /// asked instead of assumed.
    ///
    /// One relation genuinely cannot have it: Client.salesManagerId points at
    /// Client, and SQL Server refuses SET NULL on a self-reference. That one
    /// stays NO ACTION and is the single remaining difference the audit
    /// reports.
    /// </remarks>
    public partial class SchemaGapsFromTheAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "VinLookup_decodedAt_idx",
                table: "VinLookup",
                column: "decodedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TicketMessage_authorId",
                table: "TicketMessage",
                column: "authorId");

            migrationBuilder.CreateIndex(
                name: "SearchMiss_searches_idx",
                table: "SearchMiss",
                column: "searches");

            migrationBuilder.CreateIndex(
                name: "IX_PriceListImport_uploadedById",
                table: "PriceListImport",
                column: "uploadedById");

            migrationBuilder.CreateIndex(
                name: "IX_Order_statusChangedById",
                table: "Order",
                column: "statusChangedById");

            migrationBuilder.CreateIndex(
                name: "Order_status_createdAt_idx",
                table: "Order",
                columns: new[] { "status", "createdAt" });

            migrationBuilder.CreateIndex(
                name: "GoodsCategory_name_key",
                table: "GoodsCategory",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "Order_statusChangedById_fkey",
                table: "Order",
                column: "statusChangedById",
                principalTable: "Client",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "PriceListImport_uploadedById_fkey",
                table: "PriceListImport",
                column: "uploadedById",
                principalTable: "Client",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "TicketMessage_authorId_fkey",
                table: "TicketMessage",
                column: "authorId",
                principalTable: "Client",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "Order_statusChangedById_fkey",
                table: "Order");

            migrationBuilder.DropForeignKey(
                name: "PriceListImport_uploadedById_fkey",
                table: "PriceListImport");

            migrationBuilder.DropForeignKey(
                name: "TicketMessage_authorId_fkey",
                table: "TicketMessage");

            migrationBuilder.DropIndex(
                name: "VinLookup_decodedAt_idx",
                table: "VinLookup");

            migrationBuilder.DropIndex(
                name: "IX_TicketMessage_authorId",
                table: "TicketMessage");

            migrationBuilder.DropIndex(
                name: "SearchMiss_searches_idx",
                table: "SearchMiss");

            migrationBuilder.DropIndex(
                name: "IX_PriceListImport_uploadedById",
                table: "PriceListImport");

            migrationBuilder.DropIndex(
                name: "IX_Order_statusChangedById",
                table: "Order");

            migrationBuilder.DropIndex(
                name: "Order_status_createdAt_idx",
                table: "Order");

            migrationBuilder.DropIndex(
                name: "GoodsCategory_name_key",
                table: "GoodsCategory");
        }
    }
}
