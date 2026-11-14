using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeShift.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DailyReviewMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "last_evening_review",
                table: "users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "last_morning_check",
                table: "users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_evening_review",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_morning_check",
                table: "users");
        }
    }
}
