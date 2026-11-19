using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeShift.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class PingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "after_unanswered_pings",
                table: "user_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "BackOff");

            migrationBuilder.AddColumn<bool>(
                name: "pings_enabled",
                table: "user_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "quiet_end",
                table: "user_settings",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "quiet_start",
                table: "user_settings",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "after_unanswered_pings",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "pings_enabled",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "quiet_end",
                table: "user_settings");

            migrationBuilder.DropColumn(
                name: "quiet_start",
                table: "user_settings");
        }
    }
}
