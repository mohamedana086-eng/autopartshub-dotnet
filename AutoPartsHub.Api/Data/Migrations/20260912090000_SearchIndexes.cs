using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsHub.Api.Data.Migrations
{
    /// <summary>
    /// What the near-miss search seeks on: normalised part numbers, an index
    /// on product names, and a full-text catalogue where the engine has one.
    /// </summary>
    /// <remarks>
    /// Hand-written and kept separate for the same reason as the view
    /// migration beside it — EF generates neither computed columns declared
    /// outside the model nor full-text catalogues, and the initial migration
    /// is still disposable while the model moves.
    ///
    /// The SQL lives on the context so that the columns and the queries that
    /// seek on them can be read together. See
    /// <c>AutoPartsContext.SearchIndexSql</c>.
    /// </remarks>
    [DbContext(typeof(AutoPartsContext))]
    [Migration("20260912090000_SearchIndexes")]
    public partial class SearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in AutoPartsContext.SearchIndexSql)
            {
                migrationBuilder.Sql(statement);
            }

            migrationBuilder.Sql(AutoPartsContext.FullTextSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AutoPartsContext.DropFullTextSql);

            foreach (var statement in AutoPartsContext.DropSearchIndexSql)
            {
                migrationBuilder.Sql(statement);
            }
        }
    }
}
