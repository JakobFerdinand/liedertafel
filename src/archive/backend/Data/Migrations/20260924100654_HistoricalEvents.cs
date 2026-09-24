using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class HistoricalEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Venue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartTime = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SourceNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DateYear = table.Column<int>(type: "integer", nullable: true),
                    DateMonth = table.Column<int>(type: "integer", nullable: true),
                    DateDay = table.Column<int>(type: "integer", nullable: true),
                    DateApproximate = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_DateYear",
                table: "events",
                column: "DateYear");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
