using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlternateTitlesAndMovieReleaseDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlternateTitles",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DigitalReleaseDate",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PhysicalReleaseDate",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TheatricalReleaseDate",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlternateTitles",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "DigitalReleaseDate",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "PhysicalReleaseDate",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "TheatricalReleaseDate",
                table: "MediaItems");
        }
    }
}
