using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexerRssSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RssSync",
                table: "Indexers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RssSync",
                table: "Indexers");
        }
    }
}
