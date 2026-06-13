using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AwoxController.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "meshes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Service = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    MeshName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    MeshPassword = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    MeshKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meshes", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "lamps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Mac = table.Column<string>(type: "varchar(17)", maxLength: 17, nullable: false),
                    MeshId = table.Column<int>(type: "int", nullable: false),
                    Protocol = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Room = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MeshNetworkId = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lamps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lamps_meshes_MeshNetworkId",
                        column: x => x.MeshNetworkId,
                        principalTable: "meshes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_lamps_Mac",
                table: "lamps",
                column: "Mac",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lamps_MeshNetworkId",
                table: "lamps",
                column: "MeshNetworkId");

            migrationBuilder.CreateIndex(
                name: "IX_lamps_Name",
                table: "lamps",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meshes_Service",
                table: "meshes",
                column: "Service",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lamps");

            migrationBuilder.DropTable(
                name: "meshes");
        }
    }
}
