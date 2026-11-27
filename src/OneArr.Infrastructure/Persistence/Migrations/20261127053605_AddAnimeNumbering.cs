using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimeNumbering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAnime",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AbsoluteNumber",
                table: "Episodes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAnime",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "AbsoluteNumber",
                table: "Episodes");
        }
    }
}
