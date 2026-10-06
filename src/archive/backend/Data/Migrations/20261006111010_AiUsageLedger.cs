using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiUsageLedger : Migration
    {
        /// <summary>
        /// ARC-022-3: generalises the chat usage ledger in place. The table is
        /// renamed and keeps its rows; existing rows become one-call chat
        /// operations of the model they were made with, their cost moves from
        /// rounded EUR cents to micro-EUR, and their month key is recomputed
        /// in Europe/Vienna (it was UTC).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(name: "PK_chat_usage_entries", table: "chat_usage_entries");
            migrationBuilder.RenameTable(name: "chat_usage_entries", newName: "ai_usage_entries");
            migrationBuilder.RenameIndex(
                name: "IX_chat_usage_entries_YearMonth", table: "ai_usage_entries", newName: "IX_ai_usage_entries_YearMonth");
            migrationBuilder.AddPrimaryKey(name: "PK_ai_usage_entries", table: "ai_usage_entries", column: "Id");

            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId", table: "ai_usage_entries", type: "uuid", nullable: true,
                oldClrType: typeof(Guid), oldType: "uuid");
            migrationBuilder.AlterColumn<long>(
                name: "InputTokens", table: "ai_usage_entries", type: "bigint", nullable: false,
                oldClrType: typeof(int), oldType: "integer");
            migrationBuilder.AlterColumn<long>(
                name: "OutputTokens", table: "ai_usage_entries", type: "bigint", nullable: false,
                oldClrType: typeof(int), oldType: "integer");
            migrationBuilder.RenameColumn(name: "EstimatedCostEurCents", table: "ai_usage_entries", newName: "CostMicroEur");
            migrationBuilder.AlterColumn<long>(
                name: "CostMicroEur", table: "ai_usage_entries", type: "bigint", nullable: false,
                oldClrType: typeof(int), oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "Feature", table: "ai_usage_entries", type: "character varying(64)", maxLength: 64,
                nullable: false, defaultValue: "chat");
            migrationBuilder.AddColumn<string>(
                name: "Model", table: "ai_usage_entries", type: "character varying(128)", maxLength: 128,
                nullable: false, defaultValue: "gpt-5-4-mini");
            migrationBuilder.AddColumn<Guid>(
                name: "OperationId", table: "ai_usage_entries", type: "uuid", nullable: false,
                defaultValue: Guid.Empty);
            migrationBuilder.AddColumn<int>(
                name: "Calls", table: "ai_usage_entries", type: "integer", nullable: false, defaultValue: 1);
            migrationBuilder.AddColumn<long>(
                name: "ReservedMicroEur", table: "ai_usage_entries", type: "bigint", nullable: false, defaultValue: 0L);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt", table: "ai_usage_entries", type: "timestamp with time zone", nullable: false,
                defaultValue: new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero));

            migrationBuilder.Sql("""
                UPDATE ai_usage_entries SET
                    "OperationId" = "Id",
                    "UpdatedAt" = "CreatedAt",
                    "CostMicroEur" = "CostMicroEur" * 10000,
                    "YearMonth" = to_char("CreatedAt" AT TIME ZONE 'Europe/Vienna', 'YYYY-MM');
                """);

            // The defaults only filled the existing rows; the model has none.
            migrationBuilder.AlterColumn<string>(
                name: "Feature", table: "ai_usage_entries", type: "character varying(64)", maxLength: 64, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(64)", oldMaxLength: 64, oldDefaultValue: "chat");
            migrationBuilder.AlterColumn<string>(
                name: "Model", table: "ai_usage_entries", type: "character varying(128)", maxLength: 128, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(128)", oldMaxLength: 128, oldDefaultValue: "gpt-5-4-mini");
            migrationBuilder.AlterColumn<Guid>(
                name: "OperationId", table: "ai_usage_entries", type: "uuid", nullable: false,
                oldClrType: typeof(Guid), oldType: "uuid", oldDefaultValue: Guid.Empty);
            migrationBuilder.AlterColumn<int>(
                name: "Calls", table: "ai_usage_entries", type: "integer", nullable: false,
                oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 1);
            migrationBuilder.AlterColumn<long>(
                name: "ReservedMicroEur", table: "ai_usage_entries", type: "bigint", nullable: false,
                oldClrType: typeof(long), oldType: "bigint", oldDefaultValue: 0L);
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt", table: "ai_usage_entries", type: "timestamp with time zone", nullable: false,
                oldClrType: typeof(DateTimeOffset), oldType: "timestamp with time zone",
                oldDefaultValue: new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero));

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_entries_OperationId_Model_YearMonth",
                table: "ai_usage_entries",
                columns: new[] { "OperationId", "Model", "YearMonth" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "ai_budget_months",
                columns: table => new
                {
                    YearMonth = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_budget_months", x => x.YearMonth);
                });
        }

        /// <summary>
        /// Back to the chat-only ledger. Rows of other features are removed,
        /// rows without a member cannot be kept, costs are rounded up to
        /// cents and the month key returns to UTC.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ai_budget_months");
            migrationBuilder.DropIndex(name: "IX_ai_usage_entries_OperationId_Model_YearMonth", table: "ai_usage_entries");

            migrationBuilder.Sql("""
                DELETE FROM ai_usage_entries WHERE "Feature" <> 'chat' OR "AccountId" IS NULL;
                UPDATE ai_usage_entries SET
                    "CostMicroEur" = CEIL("CostMicroEur" / 10000.0),
                    "YearMonth" = to_char("CreatedAt" AT TIME ZONE 'UTC', 'YYYY-MM');
                """);

            migrationBuilder.DropColumn(name: "Feature", table: "ai_usage_entries");
            migrationBuilder.DropColumn(name: "Model", table: "ai_usage_entries");
            migrationBuilder.DropColumn(name: "OperationId", table: "ai_usage_entries");
            migrationBuilder.DropColumn(name: "Calls", table: "ai_usage_entries");
            migrationBuilder.DropColumn(name: "ReservedMicroEur", table: "ai_usage_entries");
            migrationBuilder.DropColumn(name: "UpdatedAt", table: "ai_usage_entries");

            migrationBuilder.AlterColumn<int>(
                name: "CostMicroEur", table: "ai_usage_entries", type: "integer", nullable: false,
                oldClrType: typeof(long), oldType: "bigint");
            migrationBuilder.RenameColumn(name: "CostMicroEur", table: "ai_usage_entries", newName: "EstimatedCostEurCents");
            migrationBuilder.AlterColumn<int>(
                name: "InputTokens", table: "ai_usage_entries", type: "integer", nullable: false,
                oldClrType: typeof(long), oldType: "bigint");
            migrationBuilder.AlterColumn<int>(
                name: "OutputTokens", table: "ai_usage_entries", type: "integer", nullable: false,
                oldClrType: typeof(long), oldType: "bigint");
            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId", table: "ai_usage_entries", type: "uuid", nullable: false,
                oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.DropPrimaryKey(name: "PK_ai_usage_entries", table: "ai_usage_entries");
            migrationBuilder.RenameTable(name: "ai_usage_entries", newName: "chat_usage_entries");
            migrationBuilder.RenameIndex(
                name: "IX_ai_usage_entries_YearMonth", table: "chat_usage_entries", newName: "IX_chat_usage_entries_YearMonth");
            migrationBuilder.AddPrimaryKey(name: "PK_chat_usage_entries", table: "chat_usage_entries", column: "Id");
        }
    }
}
