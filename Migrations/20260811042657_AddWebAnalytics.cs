using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace v2en.Migrations
{
    /// <inheritdoc />
    public partial class AddWebAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Purely additive: one new table plus five new RuntimeSettings columns. Nothing existing
            // is dropped, renamed or retyped, so an older build keeps working against this schema.
            //
            // The defaults below are hand-set to the SAME values as the C# property initialisers.
            // ALTER TABLE ADD COLUMN backfills the already-deployed settings row with the column
            // default, so leaving EF's generated 0/false here would silently give the live site
            // analytics-off and retention-forever, unlike a fresh install. (These defaults only ever
            // apply to this backfill — EF always writes every column explicitly on insert, which is
            // why the model itself deliberately does NOT declare HasDefaultValue: a store default on
            // a bool would make it impossible to save "false".)
            migrationBuilder.AddColumn<bool>(
                name: "AnalyticsIncludeAdmin",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnalyticsRespectDoNotTrack",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AnalyticsRetentionDays",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 90);

            // Left empty on purpose — the hash salt is generated at startup so it is unique per
            // deployment and never present in source control or a migration script.
            migrationBuilder.AddColumn<string>(
                name: "AnalyticsSalt",
                table: "RuntimeSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EnableAnalytics",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AnalyticsEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Utc = table.Column<long>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    VisitorHash = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Country = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    Continent = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    Region = table.Column<string>(type: "TEXT", maxLength: 96, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 96, nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    Timezone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EdgeColo = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    ReferrerHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ReferrerUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Browser = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Os = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    DeviceType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    IsBot = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalyticsEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_IsBot_Utc",
                table: "AnalyticsEvents",
                columns: new[] { "IsBot", "Utc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalyticsEvents_Utc",
                table: "AnalyticsEvents",
                column: "Utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "AnalyticsIncludeAdmin",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "AnalyticsRespectDoNotTrack",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "AnalyticsRetentionDays",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "AnalyticsSalt",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EnableAnalytics",
                table: "RuntimeSettings");
        }
    }
}
