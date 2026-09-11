using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <summary>
    /// The <c>BestOffer</c> view.
    /// </summary>
    /// <remarks>
    /// Its own migration, written by hand, because EF does not generate views
    /// and regenerating the initial migration would silently drop it. Kept
    /// separate rather than pasted into that file for exactly that reason: the
    /// schema migration is disposable while the model is still moving, and this
    /// is not.
    ///
    /// The SQL itself lives on the context, beside the mapping, so that the
    /// shape of the view and the entity that reads it can be compared without
    /// opening two files. See <c>AutoPartsContext.ConfigureBestOffer</c> for
    /// why PostgreSQL's DISTINCT ON becomes a window function here, and why
    /// that rewrite is exact rather than approximate.
    /// </remarks>
    [DbContext(typeof(AutoPartsContext))]
    [Migration("20260911150000_BestOfferView")]
    public partial class BestOfferView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(AutoPartsContext.BestOfferSql);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP VIEW \"BestOffer\"");
    }
}
