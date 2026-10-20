using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class LiedEntwuerfe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lied_entwuerfe",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Composer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Arranger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Lyricist = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VoiceConfiguration = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MusicalKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Lyrics = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    EditedFields = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Suggestions = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lied_entwuerfe", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lied_entwurf_dateien",
                columns: table => new
                {
                    EntwurfId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Art = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stimme = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ArtEdited = table.Column<bool>(type: "boolean", nullable: false),
                    StimmeEdited = table.Column<bool>(type: "boolean", nullable: false),
                    LesenStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LesenAnfrageId = table.Column<int>(type: "integer", nullable: false),
                    LesenMeldung = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lied_entwurf_dateien", x => new { x.EntwurfId, x.Id });
                    table.ForeignKey(
                        name: "FK_lied_entwurf_dateien_lied_entwuerfe_EntwurfId",
                        column: x => x.EntwurfId,
                        principalTable: "lied_entwuerfe",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lied_entwurf_lesungen",
                columns: table => new
                {
                    EntwurfId = table.Column<Guid>(type: "uuid", nullable: false),
                    DateiId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnfrageId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Meldung = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Felder = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lied_entwurf_lesungen", x => new { x.EntwurfId, x.DateiId });
                    table.ForeignKey(
                        name: "FK_lied_entwurf_lesungen_lied_entwurf_dateien_EntwurfId_DateiId",
                        columns: x => new { x.EntwurfId, x.DateiId },
                        principalTable: "lied_entwurf_dateien",
                        principalColumns: new[] { "EntwurfId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lied_entwuerfe_OwnerAccountId_UpdatedAt",
                table: "lied_entwuerfe",
                columns: new[] { "OwnerAccountId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lied_entwurf_lesungen");

            migrationBuilder.DropTable(
                name: "lied_entwurf_dateien");

            migrationBuilder.DropTable(
                name: "lied_entwuerfe");
        }
    }
}
