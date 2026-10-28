using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class ColdStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StorageTier",
                table: "dateien",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "hot");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TierChangedAt",
                table: "dateien",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_dateien_storage_tier",
                table: "dateien",
                sql: "\"StorageTier\" IN ('hot', 'cold')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_dateien_storage_tier",
                table: "dateien");

            migrationBuilder.DropColumn(
                name: "StorageTier",
                table: "dateien");

            migrationBuilder.DropColumn(
                name: "TierChangedAt",
                table: "dateien");
        }
    }
}
