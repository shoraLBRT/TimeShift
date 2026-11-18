using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeShift.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ClassificationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "minutes",
                table: "user_facts",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "value",
                table: "user_facts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Правила классификации — факты без минут: в прежней схеме им нет места.
            migrationBuilder.Sql("DELETE FROM user_facts WHERE value IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "value",
                table: "user_facts");

            migrationBuilder.AlterColumn<int>(
                name: "minutes",
                table: "user_facts",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
