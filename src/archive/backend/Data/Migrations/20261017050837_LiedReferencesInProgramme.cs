using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class LiedReferencesInProgramme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SongId",
                table: "programme_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "MusicalVersionId",
                table: "programme_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "ArrangementId",
                table: "programme_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "LiedId",
                table: "programme_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SongId",
                table: "performances",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "LiedId",
                table: "performances",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_programme_items_LiedId",
                table: "programme_items",
                column: "LiedId");

            migrationBuilder.CreateIndex(
                name: "IX_performances_LiedId",
                table: "performances",
                column: "LiedId");

            migrationBuilder.AddForeignKey(
                name: "FK_performances_lieder_LiedId",
                table: "performances",
                column: "LiedId",
                principalTable: "lieder",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_programme_items_lieder_LiedId",
                table: "programme_items",
                column: "LiedId",
                principalTable: "lieder",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_performances_lieder_LiedId",
                table: "performances");

            migrationBuilder.DropForeignKey(
                name: "FK_programme_items_lieder_LiedId",
                table: "programme_items");

            migrationBuilder.DropIndex(
                name: "IX_programme_items_LiedId",
                table: "programme_items");

            migrationBuilder.DropIndex(
                name: "IX_performances_LiedId",
                table: "performances");

            migrationBuilder.DropColumn(
                name: "LiedId",
                table: "programme_items");

            migrationBuilder.DropColumn(
                name: "LiedId",
                table: "performances");

            migrationBuilder.AlterColumn<Guid>(
                name: "SongId",
                table: "programme_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MusicalVersionId",
                table: "programme_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ArrangementId",
                table: "programme_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SongId",
                table: "performances",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
