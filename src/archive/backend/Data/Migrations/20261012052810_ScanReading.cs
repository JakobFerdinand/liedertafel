using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScanReading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TextSource",
                table: "extraction_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "reading_jobs",
                columns: table => new
                {
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PagesTotal = table.Column<int>(type: "integer", nullable: true),
                    PagesRead = table.Column<int>(type: "integer", nullable: false),
                    PageLimit = table.Column<int>(type: "integer", nullable: false),
                    FieldsDone = table.Column<bool>(type: "boolean", nullable: false),
                    FieldsApplied = table.Column<int>(type: "integer", nullable: false),
                    FieldsProposed = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastEnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TriggeredByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reading_jobs", x => x.RevisionId);
                    table.CheckConstraint("CK_reading_jobs_pages", "\"PagesRead\" >= 0 AND \"PageLimit\" >= 0");
                    table.CheckConstraint("CK_reading_jobs_status", "\"Status\" IN (0, 1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_reading_jobs_file_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "file_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reading_jobs_Status",
                table: "reading_jobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reading_jobs");

            migrationBuilder.DropColumn(
                name: "TextSource",
                table: "extraction_jobs");
        }
    }
}
