using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LegacyPath",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacySource",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LibraryRoots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    PathKey = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessfulScanUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryRoots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryScanPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AppliedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryScanPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PathMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourcePrefix = table.Column<string>(type: "TEXT", nullable: false),
                    TargetPrefix = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    PathKey = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LibraryRootId = table.Column<int>(type: "INTEGER", nullable: true),
                    MediaItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    Resolution = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchMethod = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastVerifiedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFiles_LibraryRoots_LibraryRootId",
                        column: x => x.LibraryRootId,
                        principalTable: "LibraryRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MediaFiles_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MediaFileEpisodes",
                columns: table => new
                {
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFileEpisodes", x => new { x.MediaFileId, x.EpisodeId });
                    table.ForeignKey(
                        name: "FK_MediaFileEpisodes_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaFileEpisodes_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryRoots_MediaType_PathKey",
                table: "LibraryRoots",
                columns: new[] { "MediaType", "PathKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFileEpisodes_EpisodeId",
                table: "MediaFileEpisodes",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_LibraryRootId",
                table: "MediaFiles",
                column: "LibraryRootId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_MediaItemId",
                table: "MediaFiles",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_PathKey",
                table: "MediaFiles",
                column: "PathKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PathMappings_MediaType_SourcePrefix",
                table: "PathMappings",
                columns: new[] { "MediaType", "SourcePrefix" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryScanPlans");

            migrationBuilder.DropTable(
                name: "MediaFileEpisodes");

            migrationBuilder.DropTable(
                name: "PathMappings");

            migrationBuilder.DropTable(
                name: "MediaFiles");

            migrationBuilder.DropTable(
                name: "LibraryRoots");

            migrationBuilder.DropColumn(
                name: "LegacyPath",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LegacySource",
                table: "MediaItems");
        }
    }
}
