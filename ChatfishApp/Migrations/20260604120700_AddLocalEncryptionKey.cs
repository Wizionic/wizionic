using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatfishApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalEncryptionKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalEncryptionKey",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalEncryptionKey",
                table: "Users");
        }
    }
}
