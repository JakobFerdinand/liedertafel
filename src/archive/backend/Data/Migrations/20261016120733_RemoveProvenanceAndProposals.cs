using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProvenanceAndProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "field_provenance");

            migrationBuilder.DropTable(
                name: "proposals");

            migrationBuilder.DropColumn(
                name: "FieldsProposed",
                table: "reading_jobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FieldsProposed",
                table: "reading_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "field_provenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Field = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Locked = table.Column<bool>(type: "boolean", nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PreviousValue = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    PromptVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false)
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
                    Confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Payload = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetEntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TargetRowVersion = table.Column<long>(type: "bigint", nullable: true)
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
    }
}
