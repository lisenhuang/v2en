using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace v2en.Migrations
{
    /// <inheritdoc />
    public partial class AddChatGptTranslation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ChatGPT (Codex) OAuth account — all additive, safe defaults so existing rows stay valid.
            migrationBuilder.AddColumn<string>(
                name: "ChatGptAccessToken", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<long>(
                name: "ChatGptAccessTokenExpiresUtc", table: "RuntimeSettings", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "ChatGptAccountId", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "ChatGptAccountLabel", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "ChatGptIdToken", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "ChatGptPlanType", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "ChatGptRefreshToken", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");

            // Translation provider routing (primary → fallback).
            migrationBuilder.AddColumn<string>(
                name: "TranslationPrimaryProvider", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "TranslationPrimaryModel", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "TranslationPrimaryReasoning", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "TranslationFallbackProvider", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "TranslationFallbackModel", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "TranslationFallbackReasoning", table: "RuntimeSettings", type: "TEXT", nullable: false, defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ChatGptAccessToken", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "ChatGptAccessTokenExpiresUtc", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "ChatGptAccountId", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "ChatGptAccountLabel", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "ChatGptIdToken", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "ChatGptPlanType", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "ChatGptRefreshToken", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "TranslationPrimaryProvider", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "TranslationPrimaryModel", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "TranslationPrimaryReasoning", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "TranslationFallbackProvider", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "TranslationFallbackModel", table: "RuntimeSettings");
            migrationBuilder.DropColumn(name: "TranslationFallbackReasoning", table: "RuntimeSettings");
        }
    }
}
