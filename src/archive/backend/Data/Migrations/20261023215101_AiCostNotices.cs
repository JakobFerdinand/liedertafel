using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiCostNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "NotifiedMicroEur",
                table: "ai_budget_months",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                comment: "ARC-043 (#150): the highest AI cost notice level (threshold or cap) the maintainers have already been notified about this month.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifiedMicroEur",
                table: "ai_budget_months");
        }
    }
}
