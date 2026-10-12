using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Brilliant.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Drafts",
                columns: table => new
                {
                    StepId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    PushedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drafts", x => x.StepId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_Seq",
                table: "Drafts",
                column: "Seq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Drafts");
        }
    }
}
