using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEventPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #160: a saved Auftritt is visible to every member. With the columns gone,
            // every event that was still unpublished is visible too.
            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "events");

            migrationBuilder.DropColumn(
                name: "PublishedByAccountId",
                table: "events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedByAccountId",
                table: "events",
                type: "uuid",
                nullable: true);
        }
    }
}
