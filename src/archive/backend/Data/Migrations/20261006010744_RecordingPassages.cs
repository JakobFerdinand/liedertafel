using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordingPassages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_recordings_Id_EventId",
                table: "recordings",
                columns: new[] { "Id", "EventId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_performances_Id_EventId",
                table: "performances",
                columns: new[] { "Id", "EventId" });

            migrationBuilder.CreateTable(
                name: "recording_passages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaybackRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartSeconds = table.Column<double>(type: "double precision", nullable: false),
                    EndSeconds = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recording_passages", x => x.Id);
                    table.CheckConstraint("CK_recording_passages_order", "\"EndSeconds\" > \"StartSeconds\"");
                    table.CheckConstraint("CK_recording_passages_start", "\"StartSeconds\" >= 0");
                    table.ForeignKey(
                        name: "FK_recording_passages_file_revisions_PlaybackRevisionId",
                        column: x => x.PlaybackRevisionId,
                        principalTable: "file_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recording_passages_performances_PerformanceId_EventId",
                        columns: x => new { x.PerformanceId, x.EventId },
                        principalTable: "performances",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recording_passages_recordings_RecordingId_EventId",
                        columns: x => new { x.RecordingId, x.EventId },
                        principalTable: "recordings",
                        principalColumns: new[] { "Id", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_PerformanceId",
                table: "recording_passages",
                column: "PerformanceId");

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_PerformanceId_EventId",
                table: "recording_passages",
                columns: new[] { "PerformanceId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_PlaybackRevisionId",
                table: "recording_passages",
                column: "PlaybackRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_RecordingId_EventId",
                table: "recording_passages",
                columns: new[] { "RecordingId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_RecordingId_PerformanceId",
                table: "recording_passages",
                columns: new[] { "RecordingId", "PerformanceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recording_passages");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_recordings_Id_EventId",
                table: "recordings");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_performances_Id_EventId",
                table: "performances");
        }
    }
}
