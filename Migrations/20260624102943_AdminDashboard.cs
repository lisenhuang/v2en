using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace v2en.Migrations
{
    /// <inheritdoc />
    public partial class AdminDashboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastLoginUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DailyQuota = table.Column<int>(type: "INTEGER", nullable: false),
                    UnlimitedDaily = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxPerTick = table.Column<int>(type: "INTEGER", nullable: false),
                    MinDelaySecondsBetweenCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    Temperature = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TranslationLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Utc = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Event = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    V2exId = table.Column<long>(type: "INTEGER", nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranslationLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Username",
                table: "AdminUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TranslationLogs_Level",
                table: "TranslationLogs",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_TranslationLogs_Utc",
                table: "TranslationLogs",
                column: "Utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "RuntimeSettings");

            migrationBuilder.DropTable(
                name: "TranslationLogs");
        }
    }
}
