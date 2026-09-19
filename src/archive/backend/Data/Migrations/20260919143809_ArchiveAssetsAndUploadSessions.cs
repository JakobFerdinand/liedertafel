using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveAssetsAndUploadSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MusicalVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "score"),
                    VoiceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
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
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    BlobName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlobName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaxSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadTicketExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    FinalizedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_upload_sessions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_upload_sessions_file_revisions_FinalizedRevisionId",
                        column: x => x.FinalizedRevisionId,
                        principalTable: "file_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assets_CurrentRevisionId",
                table: "assets",
                column: "CurrentRevisionId",
                unique: true);

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
                name: "IX_upload_sessions_AssetId",
                table: "upload_sessions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_BlobName",
                table: "upload_sessions",
                column: "BlobName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_FinalizedRevisionId",
                table: "upload_sessions",
                column: "FinalizedRevisionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_assets_file_revisions_CurrentRevisionId",
                table: "assets",
                column: "CurrentRevisionId",
                principalTable: "file_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_assets_file_revisions_CurrentRevisionId",
                table: "assets");

            migrationBuilder.DropTable(
                name: "upload_sessions");

            migrationBuilder.DropTable(
                name: "file_revisions");

            migrationBuilder.DropTable(
                name: "assets");
        }
    }
}
