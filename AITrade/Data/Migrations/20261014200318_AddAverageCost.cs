using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AITrade.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAverageCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AverageCostPerShare",
                table: "WatchlistItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AverageCostPerShare",
                table: "WatchlistItems");
        }
    }
}
