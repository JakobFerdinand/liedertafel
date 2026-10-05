using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProgrammeConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConfirmationId",
                table: "performances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProgrammeItemId",
                table: "performances",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "programme_confirmations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgrammeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_confirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_programme_confirmations_programme_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "programme_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_programme_confirmations_programmes_ProgrammeId",
                        column: x => x.ProgrammeId,
                        principalTable: "programmes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_performances_ConfirmationId",
                table: "performances",
                column: "ConfirmationId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_ProgrammeItemId",
                table: "performances",
                column: "ProgrammeItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_programme_confirmations_ProgrammeId",
                table: "programme_confirmations",
                column: "ProgrammeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_programme_confirmations_RevisionId",
                table: "programme_confirmations",
                column: "RevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_performances_programme_confirmations_ConfirmationId",
                table: "performances",
                column: "ConfirmationId",
                principalTable: "programme_confirmations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_performances_programme_items_ProgrammeItemId",
                table: "performances",
                column: "ProgrammeItemId",
                principalTable: "programme_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_performances_programme_confirmations_ConfirmationId",
                table: "performances");

            migrationBuilder.DropForeignKey(
                name: "FK_performances_programme_items_ProgrammeItemId",
                table: "performances");

            migrationBuilder.DropTable(
                name: "programme_confirmations");

            migrationBuilder.DropIndex(
                name: "IX_performances_ConfirmationId",
                table: "performances");

            migrationBuilder.DropIndex(
                name: "IX_performances_ProgrammeItemId",
                table: "performances");

            migrationBuilder.DropColumn(
                name: "ConfirmationId",
                table: "performances");

            migrationBuilder.DropColumn(
                name: "ProgrammeItemId",
                table: "performances");
        }
    }
}
