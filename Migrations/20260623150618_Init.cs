using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace v2en.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastETag = table.Column<string>(type: "TEXT", nullable: true),
                    LastSourceFeedUpdated = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFetchUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    TranslationsToday = table.Column<int>(type: "INTEGER", nullable: false),
                    QuotaWindowResetUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    V2exId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceTagId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AuthorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthorUri = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TitleZh = table.Column<string>(type: "TEXT", nullable: false),
                    ContentZhHtml = table.Column<string>(type: "TEXT", nullable: false),
                    TitleEn = table.Column<string>(type: "TEXT", nullable: true),
                    ContentEnHtml = table.Column<string>(type: "TEXT", nullable: true),
                    Published = table.Column<long>(type: "INTEGER", nullable: false),
                    Updated = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TranslationModel = table.Column<string>(type: "TEXT", nullable: true),
                    TranslatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "FeedStates",
                columns: new[] { "Id", "LastETag", "LastFetchUtc", "LastSourceFeedUpdated", "LastStatusCode", "QuotaWindowResetUtc", "TranslationsToday" },
                values: new object[] { 1, null, null, null, null, null, 0 });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Published",
                table: "Posts",
                column: "Published");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Status_Published",
                table: "Posts",
                columns: new[] { "Status", "Published" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_V2exId",
                table: "Posts",
                column: "V2exId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedStates");

            migrationBuilder.DropTable(
                name: "Posts");
        }
    }
}
