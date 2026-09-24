using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class RepertoireFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "songs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Occasion",
                table: "songs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Accompaniment",
                table: "arrangements",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "song_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    SongId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_song_tags_SongId",
                table: "song_tags",
                column: "SongId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "song_tags");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "Occasion",
                table: "songs");

            migrationBuilder.DropColumn(
                name: "Accompaniment",
                table: "arrangements");
        }
    }
}
