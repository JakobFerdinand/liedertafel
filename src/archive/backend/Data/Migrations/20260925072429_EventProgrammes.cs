using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventProgrammes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "programmes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programmes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_programmes_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "programme_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgrammeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false)
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
                name: "programme_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArrangementId = table.Column<Guid>(type: "uuid", nullable: false),
                    MusicalVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_programme_items_arrangements_ArrangementId",
                        column: x => x.ArrangementId,
                        principalTable: "arrangements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_programme_items_musical_versions_MusicalVersionId",
                        column: x => x.MusicalVersionId,
                        principalTable: "musical_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_programme_items_programme_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "programme_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_programme_items_songs_SongId",
                        column: x => x.SongId,
                        principalTable: "songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_ArrangementId",
                table: "programme_items",
                column: "ArrangementId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_MusicalVersionId",
                table: "programme_items",
                column: "MusicalVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_RevisionId_Position",
                table: "programme_items",
                columns: new[] { "RevisionId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_SongId",
                table: "programme_items",
                column: "SongId");

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

            migrationBuilder.CreateIndex(
                name: "IX_programmes_EventId",
                table: "programmes",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "programme_items");

            migrationBuilder.DropTable(
                name: "programme_revisions");

            migrationBuilder.DropTable(
                name: "programmes");
        }
    }
}
