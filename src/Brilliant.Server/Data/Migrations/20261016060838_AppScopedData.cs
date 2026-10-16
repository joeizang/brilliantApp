using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Brilliant.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppScopedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PairingCodes",
                table: "PairingCodes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Packs",
                table: "Packs");

            migrationBuilder.DropIndex(
                name: "IX_Events_Id",
                table: "Events");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Drafts",
                table: "Drafts");

            migrationBuilder.AddColumn<string>(
                name: "AppId",
                table: "PairingCodes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "com.joeizang.brilliantapp");

            migrationBuilder.AddColumn<string>(
                name: "AppId",
                table: "Packs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "com.joeizang.brilliantapp");

            migrationBuilder.AddColumn<string>(
                name: "AppId",
                table: "Events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "com.joeizang.brilliantapp");

            migrationBuilder.AddColumn<string>(
                name: "AppId",
                table: "Drafts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "com.joeizang.brilliantapp");

            migrationBuilder.AddColumn<string>(
                name: "AppId",
                table: "Devices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "com.joeizang.brilliantapp");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PairingCodes",
                table: "PairingCodes",
                columns: new[] { "AppId", "Hash" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Packs",
                table: "Packs",
                columns: new[] { "AppId", "PackId", "Major", "Minor", "Patch" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Drafts",
                table: "Drafts",
                columns: new[] { "AppId", "StepId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_AppId_Id",
                table: "Events",
                columns: new[] { "AppId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_AppId_Seq",
                table: "Events",
                columns: new[] { "AppId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_AppId_Seq",
                table: "Drafts",
                columns: new[] { "AppId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_AppId",
                table: "Devices",
                column: "AppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removing AppId would silently merge app data even when the old global keys happen not to collide.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Devices" WHERE "AppId" <> 'com.joeizang.brilliantapp')
                        OR EXISTS (SELECT 1 FROM "PairingCodes" WHERE "AppId" <> 'com.joeizang.brilliantapp')
                        OR EXISTS (SELECT 1 FROM "Events" WHERE "AppId" <> 'com.joeizang.brilliantapp')
                        OR EXISTS (SELECT 1 FROM "Drafts" WHERE "AppId" <> 'com.joeizang.brilliantapp')
                        OR EXISTS (SELECT 1 FROM "Packs" WHERE "AppId" <> 'com.joeizang.brilliantapp') THEN
                        RAISE EXCEPTION 'Cannot remove app scoping while non-DSA data exists. Preserve or remove those app records before downgrading.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_PairingCodes",
                table: "PairingCodes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Packs",
                table: "Packs");

            migrationBuilder.DropIndex(
                name: "IX_Events_AppId_Id",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_AppId_Seq",
                table: "Events");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Drafts",
                table: "Drafts");

            migrationBuilder.DropIndex(
                name: "IX_Drafts_AppId_Seq",
                table: "Drafts");

            migrationBuilder.DropIndex(
                name: "IX_Devices_AppId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "PairingCodes");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "Packs");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "Drafts");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "Devices");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PairingCodes",
                table: "PairingCodes",
                column: "Hash");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Packs",
                table: "Packs",
                columns: new[] { "PackId", "Major", "Minor", "Patch" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Drafts",
                table: "Drafts",
                column: "StepId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Id",
                table: "Events",
                column: "Id",
                unique: true);
        }
    }
}
