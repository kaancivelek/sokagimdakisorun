using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SokagimdakiSorun.Media.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.CreateTable(
                name: "media_files",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_files", x => x.id);
                    table.CheckConstraint("ck_media_files_content_type", "content_type IN ('image/jpeg', 'image/png', 'image/webp')");
                    table.CheckConstraint("ck_media_files_lifecycle", "(status = 'Temporary' AND committed_at IS NULL AND deleted_at IS NULL) OR (status = 'Committed' AND committed_at IS NOT NULL AND deleted_at IS NULL) OR (status = 'Deleted' AND deleted_at IS NOT NULL)");
                    table.CheckConstraint("ck_media_files_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_media_files_size_bytes", "size_bytes > 0");
                    table.CheckConstraint("ck_media_files_status", "\"status\" IN ('Temporary', 'Committed', 'Deleted')");
                    table.CheckConstraint("ck_media_files_storage_key", "storage_key ~ '^[0-9a-f]{32}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_files_storage_key",
                schema: "media",
                table: "media_files",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_files_temporary_created_at",
                schema: "media",
                table: "media_files",
                column: "created_at",
                filter: "status = 'Temporary'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_files",
                schema: "media");
        }
    }
}
