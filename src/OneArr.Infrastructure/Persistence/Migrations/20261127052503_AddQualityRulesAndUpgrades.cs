using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OneArr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityRulesAndUpgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedSources",
                table: "QualityProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxSizeMb",
                table: "QualityProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinSizeMb",
                table: "QualityProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MustContain",
                table: "QualityProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MustNotContain",
                table: "QualityProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredWords",
                table: "QualityProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpgradesAllowed",
                table: "QualityProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseTitle",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "MediaFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "MediaFiles",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedSources",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "MaxSizeMb",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "MinSizeMb",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "MustContain",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "MustNotContain",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "PreferredWords",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "UpgradesAllowed",
                table: "QualityProfiles");

            migrationBuilder.DropColumn(
                name: "ReleaseTitle",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "MediaFiles");
        }
    }
}
