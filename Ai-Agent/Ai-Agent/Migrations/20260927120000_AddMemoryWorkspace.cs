using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ai_Agent.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Workspace",
                table: "ConversationMemories",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Workspace",
                table: "ConversationMemories");
        }
    }
}
