using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuftrittDateien : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "OriginalAssetId",
                table: "recordings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalDateiId",
                table: "recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlaybackDateiId",
                table: "recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PlaybackRevisionId",
                table: "recording_passages",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "PlaybackDateiId",
                table: "recording_passages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_recordings_OriginalDateiId",
                table: "recordings",
                column: "OriginalDateiId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recordings_PlaybackDateiId",
                table: "recordings",
                column: "PlaybackDateiId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_recordings_dateien",
                table: "recordings",
                sql: "\"PlaybackDateiId\" IS NULL OR \"PlaybackDateiId\" <> \"OriginalDateiId\"");

            migrationBuilder.CreateIndex(
                name: "IX_recording_passages_PlaybackDateiId",
                table: "recording_passages",
                column: "PlaybackDateiId");

            migrationBuilder.AddForeignKey(
                name: "FK_recording_passages_dateien_PlaybackDateiId",
                table: "recording_passages",
                column: "PlaybackDateiId",
                principalTable: "dateien",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_recordings_dateien_OriginalDateiId",
                table: "recordings",
                column: "OriginalDateiId",
                principalTable: "dateien",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_recordings_dateien_PlaybackDateiId",
                table: "recordings",
                column: "PlaybackDateiId",
                principalTable: "dateien",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_recording_passages_dateien_PlaybackDateiId",
                table: "recording_passages");

            migrationBuilder.DropForeignKey(
                name: "FK_recordings_dateien_OriginalDateiId",
                table: "recordings");

            migrationBuilder.DropForeignKey(
                name: "FK_recordings_dateien_PlaybackDateiId",
                table: "recordings");

            migrationBuilder.DropIndex(
                name: "IX_recordings_OriginalDateiId",
                table: "recordings");

            migrationBuilder.DropIndex(
                name: "IX_recordings_PlaybackDateiId",
                table: "recordings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_recordings_dateien",
                table: "recordings");

            migrationBuilder.DropIndex(
                name: "IX_recording_passages_PlaybackDateiId",
                table: "recording_passages");

            migrationBuilder.DropColumn(
                name: "OriginalDateiId",
                table: "recordings");

            migrationBuilder.DropColumn(
                name: "PlaybackDateiId",
                table: "recordings");

            migrationBuilder.DropColumn(
                name: "PlaybackDateiId",
                table: "recording_passages");

            migrationBuilder.AlterColumn<Guid>(
                name: "OriginalAssetId",
                table: "recordings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PlaybackRevisionId",
                table: "recording_passages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
