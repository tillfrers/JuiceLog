using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JuiceLog.Migrations
{
    /// <inheritdoc />
    public partial class EstimatedReadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Estimated",
                table: "Energy",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Estimated",
                table: "Energy");
        }
    }
}
