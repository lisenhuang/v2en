using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace v2en.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingsAndChatSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatMaxContextPosts",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ChatModel",
                table: "RuntimeSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ChatRateLimitPerMinutePerIp",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EmbedMaxAttempts",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EmbedMaxPerTick",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDim",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "RuntimeSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "EnableChat",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "GeminiEmbedKeysJson",
                table: "RuntimeSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OpenRouterApiKey",
                table: "RuntimeSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RetrievalTopK",
                table: "RuntimeSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PostEmbeddings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<int>(type: "INTEGER", nullable: false),
                    Vector = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Dim = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EmbeddedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostEmbeddings_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostEmbeddings_PostId",
                table: "PostEmbeddings",
                column: "PostId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostEmbeddings");

            migrationBuilder.DropColumn(
                name: "ChatMaxContextPosts",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "ChatModel",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "ChatRateLimitPerMinutePerIp",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EmbedMaxAttempts",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EmbedMaxPerTick",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EmbeddingDim",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "EnableChat",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "GeminiEmbedKeysJson",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "OpenRouterApiKey",
                table: "RuntimeSettings");

            migrationBuilder.DropColumn(
                name: "RetrievalTopK",
                table: "RuntimeSettings");
        }
    }
}
