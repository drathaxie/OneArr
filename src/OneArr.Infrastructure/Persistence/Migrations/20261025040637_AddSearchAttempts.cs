using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemKey = table.Column<string>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchAttempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchAttempts_ItemKey",
                table: "SearchAttempts",
                column: "ItemKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchAttempts_NextAttemptAt",
                table: "SearchAttempts",
                column: "NextAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchAttempts");
        }
    }
}
