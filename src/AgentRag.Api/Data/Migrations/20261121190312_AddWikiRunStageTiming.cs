using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiRunStageTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StageStartCompleted",
                table: "WikiRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "StageStartedUtc",
                table: "WikiRuns",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StageStartCompleted",
                table: "WikiRuns");

            migrationBuilder.DropColumn(
                name: "StageStartedUtc",
                table: "WikiRuns");
        }
    }
}
