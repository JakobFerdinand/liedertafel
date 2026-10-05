using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConcertRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OriginalAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaybackAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    DownloadEnabled = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("PK_recordings", x => x.Id);
                    table.CheckConstraint("CK_recordings_kind", "\"Kind\" IN ('audio', 'video')");
                    table.ForeignKey(
                        name: "FK_recordings_assets_OriginalAssetId",
                        column: x => x.OriginalAssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recordings_assets_PlaybackAssetId",
                        column: x => x.PlaybackAssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recordings_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recordings_EventId",
                table: "recordings",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_recordings_OriginalAssetId",
                table: "recordings",
                column: "OriginalAssetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recordings_PlaybackAssetId",
                table: "recordings",
                column: "PlaybackAssetId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recordings");
        }
    }
}
