using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuftrittProgramm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #160 (ADR-0001): one editable Programm per Auftritt. The latest
            // revision's items (highest Number, working draft included) become
            // the Programm; revisions, confirmations and the evidence rows
            // (performances) are dropped. Zeitmarken (recording_passages) move
            // from a performance to the Programm entry it confirmed; a mark on
            // a performance that has no surviving entry (hand-entered evidence,
            // an entry of an older revision) cannot be mapped and is removed.
            // Accounts, Lieder, Dateien and recordings are not touched.
            migrationBuilder.DropForeignKey(
                name: "FK_recording_passages_performances_PerformanceId_EventId",
                table: "recording_passages");

            migrationBuilder.DropIndex(
                name: "IX_recording_passages_PerformanceId_EventId",
                table: "recording_passages");

            migrationBuilder.Sql("""
                DELETE FROM recording_passages rp
                WHERE NOT EXISTS (
                    SELECT 1 FROM performances p
                    JOIN programme_items i ON i."Id" = p."ProgrammeItemId"
                    WHERE p."Id" = rp."PerformanceId"
                      AND i."RevisionId" IN (SELECT DISTINCT ON ("ProgrammeId") "Id" FROM programme_revisions ORDER BY "ProgrammeId", "Number" DESC));
                """);
            migrationBuilder.Sql("""
                UPDATE recording_passages rp SET "PerformanceId" = p."ProgrammeItemId"
                FROM performances p WHERE p."Id" = rp."PerformanceId";
                """);

            migrationBuilder.DropTable(
                name: "performances");

            migrationBuilder.DropTable(
                name: "programme_confirmations");

            migrationBuilder.Sql("""
                DELETE FROM programme_items WHERE "RevisionId" NOT IN (SELECT DISTINCT ON ("ProgrammeId") "Id" FROM programme_revisions ORDER BY "ProgrammeId", "Number" DESC);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_programme_items_programme_revisions_RevisionId",
                table: "programme_items");

            migrationBuilder.Sql("""
                UPDATE programme_items i SET "RevisionId" = r."ProgrammeId"
                FROM programme_revisions r WHERE r."Id" = i."RevisionId";
                """);

            migrationBuilder.DropTable(
                name: "programme_revisions");

            migrationBuilder.RenameColumn(
                name: "PerformanceId",
                table: "recording_passages",
                newName: "ProgrammeItemId");

            migrationBuilder.RenameIndex(
                name: "IX_recording_passages_RecordingId_PerformanceId",
                table: "recording_passages",
                newName: "IX_recording_passages_RecordingId_ProgrammeItemId");

            migrationBuilder.RenameIndex(
                name: "IX_recording_passages_PerformanceId",
                table: "recording_passages",
                newName: "IX_recording_passages_ProgrammeItemId");

            migrationBuilder.RenameColumn(
                name: "RevisionId",
                table: "programme_items",
                newName: "ProgrammeId");

            migrationBuilder.RenameIndex(
                name: "IX_programme_items_RevisionId_Position",
                table: "programme_items",
                newName: "IX_programme_items_ProgrammeId_Position");

            migrationBuilder.AddForeignKey(
                name: "FK_programme_items_programmes_ProgrammeId",
                table: "programme_items",
                column: "ProgrammeId",
                principalTable: "programmes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_recording_passages_programme_items_ProgrammeItemId",
                table: "recording_passages",
                column: "ProgrammeItemId",
                principalTable: "programme_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the shape only; the dropped revisions, confirmations and evidence rows
            // are gone, so Zeitmarken (which now hang on Programm entries) cannot be mapped back.
            migrationBuilder.Sql("DELETE FROM recording_passages;");

            migrationBuilder.DropForeignKey(
                name: "FK_programme_items_programmes_ProgrammeId",
                table: "programme_items");

            migrationBuilder.DropForeignKey(
                name: "FK_recording_passages_programme_items_ProgrammeItemId",
                table: "recording_passages");

            migrationBuilder.RenameColumn(
                name: "ProgrammeItemId",
                table: "recording_passages",
                newName: "PerformanceId");

            migrationBuilder.RenameIndex(
                name: "IX_recording_passages_RecordingId_ProgrammeItemId",
                table: "recording_passages",
                newName: "IX_recording_passages_RecordingId_PerformanceId");

            migrationBuilder.RenameIndex(
                name: "IX_recording_passages_ProgrammeItemId",
                table: "recording_passages",
                newName: "IX_recording_passages_PerformanceId");

            migrationBuilder.RenameColumn(
                name: "ProgrammeId",
                table: "programme_items",
                newName: "RevisionId");

            migrationBuilder.RenameIndex(
                name: "IX_programme_items_ProgrammeId_Position",
                table: "programme_items",
                newName: "IX_programme_items_RevisionId_Position");

            migrationBuilder.CreateTable(
                name: "programme_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgrammeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedByAccountId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_programme_revisions_programmes_ProgrammeId",
                        column: x => x.ProgrammeId,
                        principalTable: "programmes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "programme_confirmations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgrammeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_confirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_programme_confirmations_programme_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "programme_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_programme_confirmations_programmes_ProgrammeId",
                        column: x => x.ProgrammeId,
                        principalTable: "programmes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "performances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    LiedId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ProgrammeItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performances", x => x.Id);
                    table.UniqueConstraint("AK_performances_Id_EventId", x => new { x.Id, x.EventId });
                    table.ForeignKey(
                        name: "FK_performances_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_performances_lieder_LiedId",
                        column: x => x.LiedId,
                        principalTable: "lieder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_performances_programme_confirmations_ConfirmationId",
                        column: x => x.ConfirmationId,
                        principalTable: "programme_confirmations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_performances_programme_items_ProgrammeItemId",
                        column: x => x.ProgrammeItemId,
                        principalTable: "programme_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_PerformanceId_EventId",
                table: "recording_passages",
                columns: new[] { "PerformanceId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_performances_ConfirmationId",
                table: "performances",
                column: "ConfirmationId");

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
                name: "IX_performances_LiedId",
                table: "performances",
                column: "LiedId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_ProgrammeItemId",
                table: "performances",
                column: "ProgrammeItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_programme_confirmations_ProgrammeId",
                table: "programme_confirmations",
                column: "ProgrammeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_programme_confirmations_RevisionId",
                table: "programme_confirmations",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_revisions_ProgrammeId",
                table: "programme_revisions",
                column: "ProgrammeId",
                unique: true,
                filter: "\"PublishedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_programme_revisions_ProgrammeId_Number",
                table: "programme_revisions",
                columns: new[] { "ProgrammeId", "Number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_programme_items_programme_revisions_RevisionId",
                table: "programme_items",
                column: "RevisionId",
                principalTable: "programme_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_recording_passages_performances_PerformanceId_EventId",
                table: "recording_passages",
                columns: new[] { "PerformanceId", "EventId" },
                principalTable: "performances",
                principalColumns: new[] { "Id", "EventId" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
