using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AwoxController.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "lamps",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "lamps");
        }
    }
}
