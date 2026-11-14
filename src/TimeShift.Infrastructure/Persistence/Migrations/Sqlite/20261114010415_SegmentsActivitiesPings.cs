using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeShift.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class SegmentsActivitiesPings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_ping_at",
                table: "users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_ping_message_ref",
                table: "users",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_seen_at",
                table: "users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "quiet_until",
                table: "users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "unanswered_pings",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    category_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    typical_duration = table.Column<TimeSpan>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activities", x => x.id);
                    table.ForeignKey(
                        name: "FK_activities_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "segments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    start_utc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    end_utc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    activity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    category_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    elapsed_before = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    is_parallel = table.Column<bool>(type: "INTEGER", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_segments", x => x.id);
                    table.ForeignKey(
                        name: "FK_segments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timeline_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    before_json = table.Column<string>(type: "TEXT", nullable: false),
                    after_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timeline_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_timeline_changes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activities_user_id_normalized_name",
                table: "activities",
                columns: new[] { "user_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_segments_user_id_start_utc",
                table: "segments",
                columns: new[] { "user_id", "start_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_timeline_changes_user_id_at",
                table: "timeline_changes",
                columns: new[] { "user_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activities");

            migrationBuilder.DropTable(
                name: "segments");

            migrationBuilder.DropTable(
                name: "timeline_changes");

            migrationBuilder.DropColumn(
                name: "last_ping_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_ping_message_ref",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "quiet_until",
                table: "users");

            migrationBuilder.DropColumn(
                name: "unanswered_pings",
                table: "users");
        }
    }
}
