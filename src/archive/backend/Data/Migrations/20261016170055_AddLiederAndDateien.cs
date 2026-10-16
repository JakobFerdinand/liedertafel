using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLiederAndDateien : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AssetId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "DateiKind",
                table: "upload_sessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "upload_sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FinalizedDateiId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPartitur",
                table: "upload_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "LiedId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecordedAtEventId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacesDateiId",
                table: "upload_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stimme",
                table: "upload_sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lieder",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Composer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Arranger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Lyricist = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VoiceConfiguration = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MusicalKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Language = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Lyrics = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    Occasion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lieder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dateien",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LiedId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsPartitur = table.Column<bool>(type: "boolean", nullable: false),
                    Stimme = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedAtEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    BlobName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacedByDateiId = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dateien", x => x.Id);
                    table.CheckConstraint("CK_dateien_kind", "\"Kind\" IN ('noten', 'uebe', 'aufnahme', 'sonstiges')");
                    table.CheckConstraint("CK_dateien_owner", "((\"LiedId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1");
                    table.CheckConstraint("CK_dateien_partitur", "NOT \"IsPartitur\" OR \"Kind\" = 'noten'");
                    table.ForeignKey(
                        name: "FK_dateien_events_EventId",
                        column: x => x.EventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dateien_events_RecordedAtEventId",
                        column: x => x.RecordedAtEventId,
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dateien_lieder_LiedId",
                        column: x => x.LiedId,
                        principalTable: "lieder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_EventId",
                table: "upload_sessions",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_LiedId",
                table: "upload_sessions",
                column: "LiedId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_upload_sessions_owner",
                table: "upload_sessions",
                sql: "((\"AssetId\" IS NOT NULL)::int + (\"LiedId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1");

            migrationBuilder.CreateIndex(
                name: "IX_dateien_BlobName",
                table: "dateien",
                column: "BlobName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dateien_EventId",
                table: "dateien",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_dateien_LiedId",
                table: "dateien",
                column: "LiedId");

            migrationBuilder.CreateIndex(
                name: "IX_dateien_RecordedAtEventId",
                table: "dateien",
                column: "RecordedAtEventId");

            migrationBuilder.CreateIndex(
                name: "IX_lieder_Title",
                table: "lieder",
                column: "Title");

            migrationBuilder.AddForeignKey(
                name: "FK_upload_sessions_events_EventId",
                table: "upload_sessions",
                column: "EventId",
                principalTable: "events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_upload_sessions_lieder_LiedId",
                table: "upload_sessions",
                column: "LiedId",
                principalTable: "lieder",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_upload_sessions_events_EventId",
                table: "upload_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_upload_sessions_lieder_LiedId",
                table: "upload_sessions");

            migrationBuilder.DropTable(
                name: "dateien");

            migrationBuilder.DropTable(
                name: "lieder");

            migrationBuilder.DropIndex(
                name: "IX_upload_sessions_EventId",
                table: "upload_sessions");

            migrationBuilder.DropIndex(
                name: "IX_upload_sessions_LiedId",
                table: "upload_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_upload_sessions_owner",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "DateiKind",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "FinalizedDateiId",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "IsPartitur",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "LiedId",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "RecordedAtEventId",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "ReplacesDateiId",
                table: "upload_sessions");

            migrationBuilder.DropColumn(
                name: "Stimme",
                table: "upload_sessions");

            migrationBuilder.AlterColumn<Guid>(
                name: "AssetId",
                table: "upload_sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
