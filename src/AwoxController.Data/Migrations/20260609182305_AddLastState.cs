using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwoxController.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLastState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastState",
                table: "lamps",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastState",
                table: "lamps");
        }
    }
}
