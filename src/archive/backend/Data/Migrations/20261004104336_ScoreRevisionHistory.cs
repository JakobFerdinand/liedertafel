using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScoreRevisionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalFileName",
                table: "file_revisions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "asset_revision_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ChangedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_revision_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_asset_revision_changes_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_asset_revision_changes_file_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "file_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_revision_changes_AssetId_ChangedAt",
                table: "asset_revision_changes",
                columns: new[] { "AssetId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_revision_changes_RevisionId",
                table: "asset_revision_changes",
                column: "RevisionId");

            // The declared upload name was only kept on the session so far.
            migrationBuilder.Sql(
                """
                UPDATE file_revisions r
                SET "OriginalFileName" = s."DeclaredFileName"
                FROM upload_sessions s
                WHERE s."FinalizedRevisionId" = r."Id" AND s."DeclaredFileName" IS NOT NULL;
                """);

            // Until this migration every revision became current when its
            // upload finalized, so the existing history is exactly one
            // upload entry per revision, replacing its predecessor.
            migrationBuilder.Sql(
                """
                INSERT INTO asset_revision_changes
                    ("Id", "AssetId", "RevisionId", "PreviousRevisionId", "Kind", "ChangedByAccountId", "ChangedAt")
                SELECT gen_random_uuid(), r."AssetId", r."Id",
                    (SELECT p."Id" FROM file_revisions p
                        WHERE p."AssetId" = r."AssetId" AND p."RevisionNumber" < r."RevisionNumber"
                        ORDER BY p."RevisionNumber" DESC LIMIT 1),
                    0, r."CreatedByAccountId", r."CreatedAt"
                FROM file_revisions r;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_revision_changes");

            migrationBuilder.DropColumn(
                name: "OriginalFileName",
                table: "file_revisions");
        }
    }
}
