using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropOldCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #172 contract step (ADR-0001, test data only). Rows that can only be
            // read through the old Song/Arrangement/Version/Asset chain are removed
            // first, children before parents, so the drops and the NOT NULL below
            // cannot fail on them. Accounts, passkeys, invitations, the AI ledger,
            // chat, events, programmes and everything of the flat model are not
            // touched. Blobs of the dropped rows are released afterwards by the
            // --cleanup-dropped-blobs command (a migration cannot reach storage).
            migrationBuilder.Sql("""
                DELETE FROM recording_passages
                WHERE "PerformanceId" IN (SELECT "Id" FROM performances WHERE "LiedId" IS NULL)
                   OR "RecordingId" IN (SELECT "Id" FROM recordings WHERE "OriginalAssetId" IS NOT NULL AND "OriginalDateiId" IS NULL);
                """);
            migrationBuilder.Sql("""
                DELETE FROM recordings WHERE "OriginalAssetId" IS NOT NULL AND "OriginalDateiId" IS NULL;
                """);
            migrationBuilder.Sql("""
                DELETE FROM performances WHERE "LiedId" IS NULL;
                """);
            migrationBuilder.Sql("""
                UPDATE performances SET "ProgrammeItemId" = NULL
                WHERE "ProgrammeItemId" IN (SELECT "Id" FROM programme_items WHERE "LiedId" IS NULL);
                """);
            migrationBuilder.Sql("""
                DELETE FROM programme_items WHERE "LiedId" IS NULL;
                """);
            migrationBuilder.Sql("""
                DELETE FROM upload_sessions WHERE "AssetId" IS NOT NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_performances_arrangements_ArrangementId",
                table: "performances");

            migrationBuilder.DropForeignKey(
                name: "FK_performances_musical_versions_MusicalVersionId",
                table: "performances");

            migrationBuilder.DropForeignKey(
                name: "FK_performances_songs_SongId",
                table: "performances");

            migrationBuilder.DropForeignKey(
                name: "FK_programme_items_arrangements_ArrangementId",
                table: "programme_items");

            migrationBuilder.DropForeignKey(
                name: "FK_programme_items_musical_versions_MusicalVersionId",
                table: "programme_items");

            migrationBuilder.DropForeignKey(
                name: "FK_programme_items_songs_SongId",
                table: "programme_items");

            migrationBuilder.DropForeignKey(
                name: "FK_recording_passages_file_revisions_PlaybackRevisionId",
                table: "recording_passages");

            migrationBuilder.DropForeignKey(
                name: "FK_recordings_assets_OriginalAssetId",
                table: "recordings");

            migrationBuilder.DropForeignKey(
                name: "FK_recordings_assets_PlaybackAssetId",
                table: "recordings");

            migrationBuilder.DropForeignKey(
                name: "FK_upload_sessions_assets_AssetId",
                table: "upload_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_upload_sessions_file_revisions_FinalizedRevisionId",
                table: "upload_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_arrangements_songs_SongId",
                table: "arrangements");

            migrationBuilder.DropForeignKey(
                name: "FK_file_revisions_assets_AssetId",
                table: "file_revisions");

            migrationBuilder.DropTable(
                name: "asset_revision_changes");

            migrationBuilder.DropTable(
                name: "song_tags");

            migrationBuilder.DropTable(
                name: "song_titles");

            migrationBuilder.DropTable(
                name: "songs");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "file_revisions");

            migrationBuilder.DropTable(
                name: "musical_versions");

            migrationBuilder.DropTable(
                name: "arrangements");

            migrationBuilder.DropIndex(
                name: "IX_upload_sessions_AssetId",
                table: "upload_sessions");

            migrationBuilder.DropIndex(
                name: "IX_upload_sessions_FinalizedRevisionId",
                table: "upload_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_upload_sessions_owner",
                table: "upload_sessions");

            migrationBuilder.DropIndex(
                name: "IX_recordings_OriginalAssetId",
                table: "recordings");

            migrationBuilder.DropIndex(
                name: "IX_recordings_PlaybackAssetId",
                table: "recordings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_recordings_slots",
                table: "recordings");

            migrationBuilder.DropIndex(
                name: "IX_recording_passages_PlaybackRevisionId",
                table: "recording_passages");

            migrationBuilder.DropIndex(
                name: "IX_programme_items_ArrangementId",
                table: "programme_items");

            migrationBuilder.DropIndex(
                name: "IX_programme_items_MusicalVersionId",
                table: "programme_items");

            migrationBuilder.DropIndex(
                name: "IX_programme_items_SongId",
                table: "programme_items");

            migrationBuilder.DropIndex(
                name: "IX_performances_ArrangementId",
                table: "performances");

            migrationBuilder.DropIndex(
                name: "IX_performances_MusicalVersionId",
                table: "performances");

            migrationBuilder.DropIndex(
                name: "IX_performances_SongId",
                table: "performances");

            migrationBuilder.DropColumn(
                name: "AssetId",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "FinalizedRevisionId",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "OriginalAssetId",
                table: "recordings");

            migrationBuilder.DropColumn(
                name: "PlaybackAssetId",
                table: "recordings");

            migrationBuilder.DropColumn(
                name: "PlaybackRevisionId",
                table: "recording_passages");

            migrationBuilder.DropColumn(
                name: "ArrangementId",
                table: "programme_items");

            migrationBuilder.DropColumn(
                name: "MusicalVersionId",
                table: "programme_items");

            migrationBuilder.DropColumn(
                name: "SongId",
                table: "programme_items");

            migrationBuilder.DropColumn(
                name: "ArrangementId",
                table: "performances");

            migrationBuilder.DropColumn(
                name: "MusicalVersionId",
                table: "performances");

            migrationBuilder.DropColumn(
                name: "SongId",
                table: "performances");

            migrationBuilder.AlterColumn<Guid>(
                name: "LiedId",
                table: "programme_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "LiedId",
                table: "performances",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_upload_sessions_owner",
                table: "upload_sessions",
                sql: "((\"LiedId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_upload_sessions_owner",
                table: "upload_sessions");

            migrationBuilder.AddColumn<Guid>(
                name: "AssetId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FinalizedRevisionId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalAssetId",
                table: "recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlaybackAssetId",
                table: "recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlaybackRevisionId",
                table: "recording_passages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "LiedId",
                table: "programme_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ArrangementId",
                table: "programme_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MusicalVersionId",
                table: "programme_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SongId",
                table: "programme_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "LiedId",
                table: "performances",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ArrangementId",
                table: "performances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MusicalVersionId",
                table: "performances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SongId",
                table: "performances",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "songs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Composer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Lyricist = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Lyrics = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    Occasion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_songs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "arrangements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    Accompaniment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Arranger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    VoiceConfiguration = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_arrangements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_arrangements_songs_SongId",
                        column: x => x.SongId,
                        principalTable: "songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "song_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_song_tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_song_tags_songs_SongId",
                        column: x => x.SongId,
                        principalTable: "songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "song_titles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_song_titles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_song_titles_songs_SongId",
                        column: x => x.SongId,
                        principalTable: "songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "musical_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArrangementId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Creator = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MusicalKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_musical_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_musical_versions_arrangements_ArrangementId",
                        column: x => x.ArrangementId,
                        principalTable: "arrangements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_revision_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_revision_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    MusicalVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "score"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    VoiceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
                    table.CheckConstraint("CK_assets_owner", "((\"MusicalVersionId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1");
                    table.ForeignKey(
                        name: "FK_assets_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assets_musical_versions_MusicalVersionId",
                        column: x => x.MusicalVersionId,
                        principalTable: "musical_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_file_revisions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_AssetId",
                table: "upload_sessions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_FinalizedRevisionId",
                table: "upload_sessions",
                column: "FinalizedRevisionId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_upload_sessions_owner",
                table: "upload_sessions",
                sql: "((\"AssetId\" IS NOT NULL)::int + (\"LiedId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1");

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

            migrationBuilder.AddCheckConstraint(
                name: "CK_recordings_slots",
                table: "recordings",
                sql: "\"PlaybackAssetId\" IS NULL OR \"PlaybackAssetId\" <> \"OriginalAssetId\"");

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_PlaybackRevisionId",
                table: "recording_passages",
                column: "PlaybackRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_ArrangementId",
                table: "programme_items",
                column: "ArrangementId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_MusicalVersionId",
                table: "programme_items",
                column: "MusicalVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_SongId",
                table: "programme_items",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_ArrangementId",
                table: "performances",
                column: "ArrangementId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_MusicalVersionId",
                table: "performances",
                column: "MusicalVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_SongId",
                table: "performances",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_arrangements_SongId",
                table: "arrangements",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_asset_revision_changes_AssetId_ChangedAt",
                table: "asset_revision_changes",
                columns: new[] { "AssetId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_revision_changes_RevisionId",
                table: "asset_revision_changes",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_assets_CurrentRevisionId",
                table: "assets",
                column: "CurrentRevisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_EventId",
                table: "assets",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_assets_MusicalVersionId",
                table: "assets",
                column: "MusicalVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_file_revisions_AssetId_RevisionNumber",
                table: "file_revisions",
                columns: new[] { "AssetId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_revisions_BlobName",
                table: "file_revisions",
                column: "BlobName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_musical_versions_ArrangementId",
                table: "musical_versions",
                column: "ArrangementId");

            migrationBuilder.CreateIndex(
                name: "IX_song_tags_SongId",
                table: "song_tags",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_song_titles_SongId",
                table: "song_titles",
                column: "SongId");

            migrationBuilder.CreateIndex(
                name: "IX_songs_Title",
                table: "songs",
                column: "Title");

            migrationBuilder.AddForeignKey(
                name: "FK_performances_arrangements_ArrangementId",
                table: "performances",
                column: "ArrangementId",
                principalTable: "arrangements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_performances_musical_versions_MusicalVersionId",
                table: "performances",
                column: "MusicalVersionId",
                principalTable: "musical_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_performances_songs_SongId",
                table: "performances",
                column: "SongId",
                principalTable: "songs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_programme_items_arrangements_ArrangementId",
                table: "programme_items",
                column: "ArrangementId",
                principalTable: "arrangements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_programme_items_musical_versions_MusicalVersionId",
                table: "programme_items",
                column: "MusicalVersionId",
                principalTable: "musical_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_programme_items_songs_SongId",
                table: "programme_items",
                column: "SongId",
                principalTable: "songs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_recording_passages_file_revisions_PlaybackRevisionId",
                table: "recording_passages",
                column: "PlaybackRevisionId",
                principalTable: "file_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_recordings_assets_OriginalAssetId",
                table: "recordings",
                column: "OriginalAssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_recordings_assets_PlaybackAssetId",
                table: "recordings",
                column: "PlaybackAssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_upload_sessions_assets_AssetId",
                table: "upload_sessions",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_upload_sessions_file_revisions_FinalizedRevisionId",
                table: "upload_sessions",
                column: "FinalizedRevisionId",
                principalTable: "file_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_revision_changes_assets_AssetId",
                table: "asset_revision_changes",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_revision_changes_file_revisions_RevisionId",
                table: "asset_revision_changes",
                column: "RevisionId",
                principalTable: "file_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_assets_file_revisions_CurrentRevisionId",
                table: "assets",
                column: "CurrentRevisionId",
                principalTable: "file_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
