using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataRefreshedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MetadataRefreshedUtc",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataRefreshedUtc",
                table: "MediaItems");
        }
    }
}
