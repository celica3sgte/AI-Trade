using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AITrade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharesOwned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SharesOwned",
                table: "WatchlistItems",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharesOwned",
                table: "WatchlistItems");
        }
    }
}
