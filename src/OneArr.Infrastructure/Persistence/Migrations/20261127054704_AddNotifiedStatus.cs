using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifiedStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotifiedStatus",
                table: "DownloadJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
            // Existing downloads count as already announced, so turning notifications on never replays old history.
            migrationBuilder.Sql("UPDATE DownloadJobs SET NotifiedStatus = Status;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifiedStatus",
                table: "DownloadJobs");
        }
    }
}
