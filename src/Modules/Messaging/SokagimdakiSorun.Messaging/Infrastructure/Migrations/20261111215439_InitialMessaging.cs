using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SokagimdakiSorun.Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    business_phone_number_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_contact_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_inbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    awaiting_reply_complaint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    awaiting_reply_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                    table.CheckConstraint("ck_conversations_awaiting_reply", "awaiting_reply_complaint_id IS NULL OR awaiting_reply_changed_at IS NOT NULL");
                    table.CheckConstraint("ck_conversations_channel", "\"channel\" IN ('WhatsApp')");
                    table.CheckConstraint("ck_conversations_ids_not_blank", "length(btrim(business_phone_number_id)) > 0 AND length(btrim(external_contact_id)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "webhook_inbox_events",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_inbox_events", x => x.id);
                    table.CheckConstraint("ck_webhook_inbox_events_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_webhook_inbox_events_dedup_key", "dedup_key ~ '^(msg|status):'");
                    table.CheckConstraint("ck_webhook_inbox_events_event_type", "\"event_type\" IN ('Message', 'Status')");
                    table.CheckConstraint("ck_webhook_inbox_events_processed_at", "(processing_state = 'Processed') = (processed_at IS NOT NULL)");
                    table.CheckConstraint("ck_webhook_inbox_events_processing_state", "\"processing_state\" IN ('Pending', 'Processing', 'Processed', 'DeadLetter')");
                    table.CheckConstraint("ck_webhook_inbox_events_provider", "\"provider\" IN ('WhatsApp')");
                });

            migrationBuilder.CreateTable(
                name: "intake_sessions",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_menu = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    description_draft = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    address_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    media_file_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    return_to_confirmation = table.Column<bool>(type: "boolean", nullable: false),
                    invalid_input_count = table.Column<int>(type: "integer", nullable: false),
                    submitted_complaint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_sessions", x => x.id);
                    table.CheckConstraint("ck_intake_sessions_completed_at", "(state IN ('Started', 'AwaitingCategory', 'AwaitingDescription', 'AwaitingLocation', 'AwaitingMedia', 'AwaitingConfirmation')) = (completed_at IS NULL)");
                    table.CheckConstraint("ck_intake_sessions_coordinates_pair", "(latitude IS NULL) = (longitude IS NULL)");
                    table.CheckConstraint("ck_intake_sessions_invalid_input_count", "invalid_input_count >= 0");
                    table.CheckConstraint("ck_intake_sessions_latitude", "latitude IS NULL OR (latitude >= -90 AND latitude <= 90)");
                    table.CheckConstraint("ck_intake_sessions_longitude", "longitude IS NULL OR (longitude >= -180 AND longitude <= 180)");
                    table.CheckConstraint("ck_intake_sessions_state", "\"state\" IN ('Started', 'AwaitingCategory', 'AwaitingDescription', 'AwaitingLocation', 'AwaitingMedia', 'AwaitingConfirmation', 'Submitted', 'Cancelled', 'Expired', 'NeedsHumanHelp')");
                    table.CheckConstraint("ck_intake_sessions_submitted", "(state = 'Submitted') = (submitted_complaint_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_intake_sessions_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "messaging",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "messaging",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    message_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_message_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    text_body = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    media_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    intake_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    complaint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    provider_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.CheckConstraint("ck_messages_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_messages_coordinates_pair", "(latitude IS NULL) = (longitude IS NULL)");
                    table.CheckConstraint("ck_messages_delivery_status", "\"delivery_status\" IN ('Received', 'Queued', 'Sending', 'Sent', 'Delivered', 'Read', 'Failed')");
                    table.CheckConstraint("ck_messages_direction", "\"direction\" IN ('Inbound', 'Outbound')");
                    table.CheckConstraint("ck_messages_direction_status", "(direction = 'Inbound') = (delivery_status = 'Received')");
                    table.CheckConstraint("ck_messages_inbound_provider_id", "direction <> 'Inbound' OR provider_message_id IS NOT NULL");
                    table.CheckConstraint("ck_messages_latitude", "latitude IS NULL OR (latitude >= -90 AND latitude <= 90)");
                    table.CheckConstraint("ck_messages_longitude", "longitude IS NULL OR (longitude >= -180 AND longitude <= 180)");
                    table.CheckConstraint("ck_messages_message_type", "\"message_type\" IN ('Text', 'Location', 'Image', 'Unsupported')");
                    table.CheckConstraint("ck_messages_outbound_fields", "direction <> 'Outbound' OR (dedup_key IS NOT NULL AND text_body IS NOT NULL)");
                    table.CheckConstraint("ck_messages_outbound_pending_schedule", "delivery_status NOT IN ('Queued', 'Sending') OR next_attempt_at IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "messaging",
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messages_intake_sessions_intake_session_id",
                        column: x => x.intake_session_id,
                        principalSchema: "messaging",
                        principalTable: "intake_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_reporter_user_id",
                schema: "messaging",
                table: "conversations",
                column: "reporter_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_conversations_business_contact",
                schema: "messaging",
                table: "conversations",
                columns: new[] { "business_phone_number_id", "external_contact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_sessions_active_expires_at",
                schema: "messaging",
                table: "intake_sessions",
                column: "expires_at",
                filter: "state IN ('Started', 'AwaitingCategory', 'AwaitingDescription', 'AwaitingLocation', 'AwaitingMedia', 'AwaitingConfirmation')");

            migrationBuilder.CreateIndex(
                name: "ux_intake_sessions_active_conversation",
                schema: "messaging",
                table: "intake_sessions",
                column: "conversation_id",
                unique: true,
                filter: "state IN ('Started', 'AwaitingCategory', 'AwaitingDescription', 'AwaitingLocation', 'AwaitingMedia', 'AwaitingConfirmation')");

            migrationBuilder.CreateIndex(
                name: "ux_intake_sessions_submitted_complaint_id",
                schema: "messaging",
                table: "intake_sessions",
                column: "submitted_complaint_id",
                unique: true,
                filter: "submitted_complaint_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_complaint_id",
                schema: "messaging",
                table: "messages",
                column: "complaint_id",
                filter: "complaint_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_created_at",
                schema: "messaging",
                table: "messages",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_intake_session_id",
                schema: "messaging",
                table: "messages",
                column: "intake_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_outbound_pending",
                schema: "messaging",
                table: "messages",
                column: "next_attempt_at",
                filter: "direction = 'Outbound' AND delivery_status IN ('Queued', 'Sending')");

            migrationBuilder.CreateIndex(
                name: "ux_messages_dedup_key",
                schema: "messaging",
                table: "messages",
                column: "dedup_key",
                unique: true,
                filter: "dedup_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_messages_provider_message_id",
                schema: "messaging",
                table: "messages",
                column: "provider_message_id",
                unique: true,
                filter: "provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_inbox_events_pending",
                schema: "messaging",
                table: "webhook_inbox_events",
                column: "next_attempt_at",
                filter: "processing_state IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "ux_webhook_inbox_events_dedup_key",
                schema: "messaging",
                table: "webhook_inbox_events",
                column: "dedup_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "messages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "webhook_inbox_events",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "intake_sessions",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "messaging");
        }
    }
}
