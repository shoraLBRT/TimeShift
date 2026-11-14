using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeShift.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ScheduleBlocksAndDialog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dialog_state",
                table: "users",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_onboarded",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<double>(
                name: "norm_factor",
                table: "user_settings",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.CreateTable(
                name: "schedule_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    category_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    activity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    days_of_week = table.Column<int>(type: "INTEGER", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_blocks", x => x.id);
                    table.ForeignKey(
                        name: "FK_schedule_blocks_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_blocks_user_id",
                table: "schedule_blocks",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_blocks");

            migrationBuilder.DropColumn(
                name: "dialog_state",
                table: "users");

            migrationBuilder.DropColumn(
                name: "is_onboarded",
                table: "users");

            migrationBuilder.AlterColumn<double>(
                name: "norm_factor",
                table: "user_settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);
        }
    }
}
