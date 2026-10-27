using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadClientTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DownloadClientId",
                table: "DownloadJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientName",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "DownloadSpeedBps",
                table: "DownloadJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "EtaSeconds",
                table: "DownloadJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1L);

            migrationBuilder.AddColumn<bool>(
                name: "ImportFailed",
                table: "DownloadJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "DownloadJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_Status",
                table: "DownloadJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_MediaItemId_EpisodeId_Status",
                table: "DownloadJobs",
                columns: new[] { "MediaItemId", "EpisodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadJobs_DownloadClientId",
                table: "DownloadJobs",
                column: "DownloadClientId");

            migrationBuilder.CreateTable(
                name: "DownloadClientHealth",
                columns: table => new
                {
                    DownloadClientId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadClientHealth", x => x.DownloadClientId);
                });

            migrationBuilder.CreateTable(
                name: "ImportPreviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DownloadJobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    PlansJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportPreviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportPreviews_DownloadJobId",
                table: "ImportPreviews",
                column: "DownloadJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadClientHealth");

            migrationBuilder.DropTable(
                name: "ImportPreviews");

            migrationBuilder.DropIndex(
                name: "IX_DownloadJobs_Status",
                table: "DownloadJobs");

            migrationBuilder.DropIndex(
                name: "IX_DownloadJobs_MediaItemId_EpisodeId_Status",
                table: "DownloadJobs");

            migrationBuilder.DropIndex(
                name: "IX_DownloadJobs_DownloadClientId",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "DownloadClientId",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "ClientName",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "DownloadSpeedBps",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "EtaSeconds",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "ImportFailed",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "DownloadJobs");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "DownloadJobs");
        }
    }
}
