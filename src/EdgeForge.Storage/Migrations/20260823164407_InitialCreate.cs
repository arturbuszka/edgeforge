using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeForge.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Meals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    PeakCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ObservedFrames = table.Column<long>(type: "INTEGER", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Meals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Meals_Day",
                table: "Meals",
                column: "Day");

            migrationBuilder.CreateIndex(
                name: "IX_Meals_StartedTicks",
                table: "Meals",
                column: "StartedTicks",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Meals");
        }
    }
}
