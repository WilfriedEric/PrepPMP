using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PmpStudy.Web.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class OfflineSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "Sessions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "ReadingLogs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "Notes",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPullUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPushUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDevices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ClientId",
                table: "Sessions",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingLogs_ClientId",
                table: "ReadingLogs",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notes_ClientId",
                table: "Notes",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncDevices_DeviceId",
                table: "SyncDevices",
                column: "DeviceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncDevices");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_ClientId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_ReadingLogs_ClientId",
                table: "ReadingLogs");

            migrationBuilder.DropIndex(
                name: "IX_Notes_ClientId",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "ReadingLogs");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Notes");
        }
    }
}
