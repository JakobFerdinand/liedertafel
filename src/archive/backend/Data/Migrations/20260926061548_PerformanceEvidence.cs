using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerformanceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "performances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArrangementId = table.Column<Guid>(type: "uuid", nullable: true),
                    MusicalVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    EvidenceStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_performances_arrangements_ArrangementId",
                        column: x => x.ArrangementId,
                        principalTable: "arrangements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_performances_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_performances_musical_versions_MusicalVersionId",
                        column: x => x.MusicalVersionId,
                        principalTable: "musical_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_performances_songs_SongId",
                        column: x => x.SongId,
                        principalTable: "songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_performances_ArrangementId",
                table: "performances",
                column: "ArrangementId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_EventId",
                table: "performances",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_EventId_IdempotencyKey",
                table: "performances",
                columns: new[] { "EventId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_performances_EventId_Position",
                table: "performances",
                columns: new[] { "EventId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_performances_MusicalVersionId",
                table: "performances",
                column: "MusicalVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_SongId",
                table: "performances",
                column: "SongId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "performances");
        }
    }
}
