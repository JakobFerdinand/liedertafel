using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class DateiExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows were keyed by file revision; they cannot map to Dateien.
            // The text is derivable and the old catalogue is test data that
            // the contract step (#172) drops (ADR-0001), so the rows go.
            migrationBuilder.Sql("DELETE FROM reading_jobs;");
            migrationBuilder.Sql("DELETE FROM extraction_jobs;");

            migrationBuilder.DropForeignKey(
                name: "FK_extraction_jobs_file_revisions_RevisionId",
                table: "extraction_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_reading_jobs_file_revisions_RevisionId",
                table: "reading_jobs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_reading_jobs",
                table: "reading_jobs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_extraction_jobs",
                table: "extraction_jobs");

            migrationBuilder.DropColumn(
                name: "RevisionId",
                table: "reading_jobs");

            migrationBuilder.DropColumn(
                name: "RevisionId",
                table: "extraction_jobs");

            migrationBuilder.RenameColumn(
                name: "AssetId",
                table: "reading_jobs",
                newName: "DateiId");

            migrationBuilder.RenameColumn(
                name: "AssetId",
                table: "extraction_jobs",
                newName: "DateiId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_reading_jobs",
                table: "reading_jobs",
                column: "DateiId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_extraction_jobs",
                table: "extraction_jobs",
                column: "DateiId");

            migrationBuilder.AddForeignKey(
                name: "FK_extraction_jobs_dateien_DateiId",
                table: "extraction_jobs",
                column: "DateiId",
                principalTable: "dateien",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_reading_jobs_dateien_DateiId",
                table: "reading_jobs",
                column: "DateiId",
                principalTable: "dateien",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_extraction_jobs_dateien_DateiId",
                table: "extraction_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_reading_jobs_dateien_DateiId",
                table: "reading_jobs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_reading_jobs",
                table: "reading_jobs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_extraction_jobs",
                table: "extraction_jobs");

            migrationBuilder.RenameColumn(
                name: "DateiId",
                table: "reading_jobs",
                newName: "AssetId");

            migrationBuilder.RenameColumn(
                name: "DateiId",
                table: "extraction_jobs",
                newName: "AssetId");

            migrationBuilder.AddColumn<Guid>(
                name: "RevisionId",
                table: "reading_jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RevisionId",
                table: "extraction_jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_reading_jobs",
                table: "reading_jobs",
                column: "RevisionId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_extraction_jobs",
                table: "extraction_jobs",
                column: "RevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_extraction_jobs_file_revisions_RevisionId",
                table: "extraction_jobs",
                column: "RevisionId",
                principalTable: "file_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_reading_jobs_file_revisions_RevisionId",
                table: "reading_jobs",
                column: "RevisionId",
                principalTable: "file_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
