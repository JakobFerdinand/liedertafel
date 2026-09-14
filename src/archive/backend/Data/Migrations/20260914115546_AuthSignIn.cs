using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "auth_request_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    IpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_request_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "auth_sign_in_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_sign_in_codes", x => x.Id);
                    table.CheckConstraint("CK_auth_sign_in_codes_attempt_count", "\"AttemptCount\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "auth_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    InvitedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    InvitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_auth_memberships_auth_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "auth_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auth_accounts_NormalizedEmail",
                table: "auth_accounts",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auth_memberships_AccountId",
                table: "auth_memberships",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_memberships_Status",
                table: "auth_memberships",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_auth_request_log_IpHash_OccurredAt",
                table: "auth_request_log",
                columns: new[] { "IpHash", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_auth_request_log_NormalizedEmail_OccurredAt",
                table: "auth_request_log",
                columns: new[] { "NormalizedEmail", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_auth_sign_in_codes_AccountId",
                table: "auth_sign_in_codes",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_sign_in_codes_ExpiresAt",
                table: "auth_sign_in_codes",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_auth_sign_in_codes_NormalizedEmail",
                table: "auth_sign_in_codes",
                column: "NormalizedEmail");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_memberships");

            migrationBuilder.DropTable(
                name: "auth_request_log");

            migrationBuilder.DropTable(
                name: "auth_sign_in_codes");

            migrationBuilder.DropTable(
                name: "auth_accounts");
        }
    }
}
