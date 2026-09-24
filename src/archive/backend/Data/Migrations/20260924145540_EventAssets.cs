using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "MusicalVersionId",
                table: "assets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_EventId",
                table: "assets",
                column: "EventId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_assets_owner",
                table: "assets",
                sql: "((\"MusicalVersionId\" IS NOT NULL)::int + (\"EventId\" IS NOT NULL)::int) = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_assets_events_EventId",
                table: "assets",
                column: "EventId",
                principalTable: "events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        // Down() re-adds NOT NULL on MusicalVersionId (with an empty-GUID
        // default) and therefore only works while no event-owned asset rows
        // exist; it is a dev-only path, never for databases holding event
        // assets.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_assets_events_EventId",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_assets_EventId",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_assets_owner",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "assets");

            migrationBuilder.AlterColumn<Guid>(
                name: "MusicalVersionId",
                table: "assets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
