using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SokagimdakiSorun.Complaints.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialComplaints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "complaints");

            migrationBuilder.CreateTable(
                name: "complaint_categories",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complaint_categories", x => x.id);
                    table.CheckConstraint("ck_complaint_categories_code", "code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_complaint_categories_name_not_blank", "length(btrim(name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "service_units",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_units", x => x.id);
                    table.CheckConstraint("ck_service_units_code", "code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_service_units_name_not_blank", "length(btrim(name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "complaints",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_reference = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    awaiting_information_entry_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    source_channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_intake_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duplicate_of_complaint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    address_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    location_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complaints", x => x.id);
                    table.CheckConstraint("ck_complaints_awaiting_entry", "(status = 'AwaitingInformation' AND awaiting_information_entry_status IN ('UnderReview', 'InProgress')) OR (status <> 'AwaitingInformation' AND awaiting_information_entry_status IS NULL)");
                    table.CheckConstraint("ck_complaints_awaiting_information_entry_status", "\"awaiting_information_entry_status\" IN ('Submitted', 'UnderReview', 'AwaitingInformation', 'Accepted', 'Assigned', 'InProgress', 'Resolved', 'Closed', 'Rejected', 'Duplicate', 'Spam', 'Withdrawn')");
                    table.CheckConstraint("ck_complaints_closed_at", "status <> 'Closed' OR closed_at IS NOT NULL");
                    table.CheckConstraint("ck_complaints_coordinates_pair", "(latitude IS NULL) = (longitude IS NULL)");
                    table.CheckConstraint("ck_complaints_description_not_blank", "length(btrim(description)) > 0");
                    table.CheckConstraint("ck_complaints_duplicate_target", "(status = 'Duplicate') = (duplicate_of_complaint_id IS NOT NULL)");
                    table.CheckConstraint("ck_complaints_latitude", "latitude IS NULL OR (latitude >= -90 AND latitude <= 90)");
                    table.CheckConstraint("ck_complaints_location_present", "latitude IS NOT NULL OR (address_text IS NOT NULL AND length(btrim(address_text)) > 0)");
                    table.CheckConstraint("ck_complaints_location_source", "\"location_source\" IN ('WhatsAppPin', 'TextAddress')");
                    table.CheckConstraint("ck_complaints_longitude", "longitude IS NULL OR (longitude >= -180 AND longitude <= 180)");
                    table.CheckConstraint("ck_complaints_not_duplicate_of_self", "duplicate_of_complaint_id IS NULL OR duplicate_of_complaint_id <> id");
                    table.CheckConstraint("ck_complaints_public_reference", "public_reference ~ '^SS-[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}$'");
                    table.CheckConstraint("ck_complaints_resolved_at", "status <> 'Resolved' OR resolved_at IS NOT NULL");
                    table.CheckConstraint("ck_complaints_source_channel", "\"source_channel\" IN ('WhatsApp')");
                    table.CheckConstraint("ck_complaints_status", "\"status\" IN ('Submitted', 'UnderReview', 'AwaitingInformation', 'Accepted', 'Assigned', 'InProgress', 'Resolved', 'Closed', 'Rejected', 'Duplicate', 'Spam', 'Withdrawn')");
                    table.ForeignKey(
                        name: "fk_complaints_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "complaints",
                        principalTable: "complaint_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_complaints_complaints_duplicate_of_complaint_id",
                        column: x => x.duplicate_of_complaint_id,
                        principalSchema: "complaints",
                        principalTable: "complaints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "routing_rules",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    district = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routing_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_routing_rules_complaint_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "complaints",
                        principalTable: "complaint_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_routing_rules_service_units_service_unit_id",
                        column: x => x.service_unit_id,
                        principalSchema: "complaints",
                        principalTable: "service_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_unit_members",
                schema: "complaints",
                columns: table => new
                {
                    service_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_unit_members", x => new { x.service_unit_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_service_unit_members_service_units_service_unit_id",
                        column: x => x.service_unit_id,
                        principalSchema: "complaints",
                        principalTable: "service_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "complaint_assignments",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    unassigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unassigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unassign_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complaint_assignments", x => x.id);
                    table.CheckConstraint("ck_complaint_assignments_unassigned_after_assigned", "unassigned_at IS NULL OR unassigned_at >= assigned_at");
                    table.ForeignKey(
                        name: "fk_complaint_assignments_complaints_complaint_id",
                        column: x => x.complaint_id,
                        principalSchema: "complaints",
                        principalTable: "complaints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_complaint_assignments_service_units_service_unit_id",
                        column: x => x.service_unit_id,
                        principalSchema: "complaints",
                        principalTable: "service_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "complaint_attachments",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complaint_attachments", x => x.id);
                    table.CheckConstraint("ck_complaint_attachments_source", "\"source\" IN ('Intake', 'InformationReply', 'Panel')");
                    table.ForeignKey(
                        name: "fk_complaint_attachments_complaints_complaint_id",
                        column: x => x.complaint_id,
                        principalSchema: "complaints",
                        principalTable: "complaints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "complaint_comments",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complaint_comments", x => x.id);
                    table.CheckConstraint("ck_complaint_comments_author_kind", "\"author_kind\" IN ('Staff', 'Reporter')");
                    table.CheckConstraint("ck_complaint_comments_body_not_blank", "length(btrim(body)) > 0");
                    table.CheckConstraint("ck_complaint_comments_source", "\"source\" IN ('Panel', 'WhatsApp')");
                    table.CheckConstraint("ck_complaint_comments_visibility", "\"visibility\" IN ('Internal', 'ReporterVisible')");
                    table.ForeignKey(
                        name: "fk_complaint_comments_complaints_complaint_id",
                        column: x => x.complaint_id,
                        principalSchema: "complaints",
                        principalTable: "complaints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "complaint_status_history",
                schema: "complaints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    complaint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reporter_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    source_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_complaint_status_history", x => x.id);
                    table.CheckConstraint("ck_complaint_status_history_actor", "(actor_kind = 'System') = (actor_user_id IS NULL)");
                    table.CheckConstraint("ck_complaint_status_history_actor_kind", "\"actor_kind\" IN ('Staff', 'Reporter', 'System')");
                    table.CheckConstraint("ck_complaint_status_history_from_status", "\"from_status\" IN ('Submitted', 'UnderReview', 'AwaitingInformation', 'Accepted', 'Assigned', 'InProgress', 'Resolved', 'Closed', 'Rejected', 'Duplicate', 'Spam', 'Withdrawn')");
                    table.CheckConstraint("ck_complaint_status_history_source", "\"source\" IN ('Panel', 'WhatsApp', 'System')");
                    table.CheckConstraint("ck_complaint_status_history_to_status", "\"to_status\" IN ('Submitted', 'UnderReview', 'AwaitingInformation', 'Accepted', 'Assigned', 'InProgress', 'Resolved', 'Closed', 'Rejected', 'Duplicate', 'Spam', 'Withdrawn')");
                    table.ForeignKey(
                        name: "fk_complaint_status_history_complaints_complaint_id",
                        column: x => x.complaint_id,
                        principalSchema: "complaints",
                        principalTable: "complaints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_complaint_assignments_active_service_unit",
                schema: "complaints",
                table: "complaint_assignments",
                columns: new[] { "service_unit_id", "complaint_id" },
                filter: "unassigned_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_complaint_assignments_complaint_id_assigned_at",
                schema: "complaints",
                table: "complaint_assignments",
                columns: new[] { "complaint_id", "assigned_at" });

            migrationBuilder.CreateIndex(
                name: "ux_complaint_assignments_active",
                schema: "complaints",
                table: "complaint_assignments",
                column: "complaint_id",
                unique: true,
                filter: "unassigned_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_complaint_attachments_complaint_id_created_at",
                schema: "complaints",
                table: "complaint_attachments",
                columns: new[] { "complaint_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_complaint_attachments_media_file_id",
                schema: "complaints",
                table: "complaint_attachments",
                column: "media_file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_complaint_categories_code",
                schema: "complaints",
                table: "complaint_categories",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_complaint_comments_complaint_id_created_at",
                schema: "complaints",
                table: "complaint_comments",
                columns: new[] { "complaint_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_complaint_status_history_complaint_id_occurred_at",
                schema: "complaints",
                table: "complaint_status_history",
                columns: new[] { "complaint_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_complaint_status_history_source_message_id",
                schema: "complaints",
                table: "complaint_status_history",
                column: "source_message_id",
                unique: true,
                filter: "source_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_complaints_category_status_created_at",
                schema: "complaints",
                table: "complaints",
                columns: new[] { "category_id", "status", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_complaints_duplicate_of_complaint_id",
                schema: "complaints",
                table: "complaints",
                column: "duplicate_of_complaint_id");

            migrationBuilder.CreateIndex(
                name: "ix_complaints_reporter_user_id",
                schema: "complaints",
                table: "complaints",
                column: "reporter_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_complaints_status_created_at",
                schema: "complaints",
                table: "complaints",
                columns: new[] { "status", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_complaints_public_reference",
                schema: "complaints",
                table: "complaints",
                column: "public_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_complaints_source_intake_session_id",
                schema: "complaints",
                table: "complaints",
                column: "source_intake_session_id",
                unique: true,
                filter: "source_intake_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_routing_rules_service_unit_id",
                schema: "complaints",
                table: "routing_rules",
                column: "service_unit_id");

            migrationBuilder.CreateIndex(
                name: "ux_routing_rules_category_service_unit",
                schema: "complaints",
                table: "routing_rules",
                columns: new[] { "category_id", "service_unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_unit_members_user_id",
                schema: "complaints",
                table: "service_unit_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_service_units_code",
                schema: "complaints",
                table: "service_units",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "complaint_assignments",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "complaint_attachments",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "complaint_comments",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "complaint_status_history",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "routing_rules",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "service_unit_members",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "complaints",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "service_units",
                schema: "complaints");

            migrationBuilder.DropTable(
                name: "complaint_categories",
                schema: "complaints");
        }
    }
}
