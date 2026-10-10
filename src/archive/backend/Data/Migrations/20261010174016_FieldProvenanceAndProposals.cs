using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class FieldProvenanceAndProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                table: "musical_versions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                table: "arrangements",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "field_provenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PreviousValue = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Locked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_provenance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetEntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Payload = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TargetRowVersion = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByAccountId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proposals", x => x.Id);
                    table.CheckConstraint("CK_proposals_kind", "\"Kind\" IN ('FieldSuggestion', 'SongCreation', 'SongPublication', 'SongDeletion', 'SongMerge', 'MemberAdministration', 'EventPublication', 'EventDeletion')");
                    table.CheckConstraint("CK_proposals_status", "\"Status\" IN ('Open', 'Accepted', 'Rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_field_provenance_target",
                table: "field_provenance",
                columns: new[] { "EntityType", "EntityId", "Field" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_proposals_status_created",
                table: "proposals",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_provenance");

            migrationBuilder.DropTable(
                name: "proposals");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "musical_versions");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "arrangements");
        }
    }
}
