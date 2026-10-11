using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Brilliant.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LessonId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StepId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Data = table.Column<string>(type: "text", nullable: true),
                    PushedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Seq);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Id",
                table: "Events",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
