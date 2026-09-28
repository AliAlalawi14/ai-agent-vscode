using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ai_Agent.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationMemories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    UserRequest = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    KeyTerms = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FilesModified = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    BuildSucceeded = table.Column<bool>(type: "boolean", nullable: false),
                    IterationsUsed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMemories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMemories_CreatedAt",
                table: "ConversationMemories",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMemories_SessionId",
                table: "ConversationMemories",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMemories");
        }
    }
}
