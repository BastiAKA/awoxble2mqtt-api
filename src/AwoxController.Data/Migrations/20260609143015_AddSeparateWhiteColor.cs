using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwoxController.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeparateWhiteColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeparateWhiteColor",
                table: "lamps",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeparateWhiteColor",
                table: "lamps");
        }
    }
}
