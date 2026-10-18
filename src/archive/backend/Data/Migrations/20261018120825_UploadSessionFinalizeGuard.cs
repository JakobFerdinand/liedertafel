using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Archive.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class UploadSessionFinalizeGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_upload_sessions_FinalizedDateiId",
                table: "upload_sessions",
                column: "FinalizedDateiId",
                unique: true,
                filter: "\"FinalizedDateiId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_upload_sessions_FinalizedDateiId",
                table: "upload_sessions");
        }
    }
}
