using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AITrade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignalPreferences",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Threshold = table.Column<double>(type: "float", nullable: false),
                    EnabledStrategies = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartingCash = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CostBps = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalPreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_SignalPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignalPreferences");
        }
    }
}
