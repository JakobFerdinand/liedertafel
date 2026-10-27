using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuftrittEntwuerfe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auftritt_entwuerfe",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DateiCount = table.Column<int>(type: "integer", nullable: false),
                    Form = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auftritt_entwuerfe", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auftritt_entwuerfe_OwnerAccountId_UpdatedAt",
                table: "auftritt_entwuerfe",
                columns: new[] { "OwnerAccountId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auftritt_entwuerfe");
        }
    }
}
