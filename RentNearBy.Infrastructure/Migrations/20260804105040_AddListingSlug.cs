using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentNearBy.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddListingSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "RoomListings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "PlotListings",
                type: "text",
                nullable: true);

            // Deterministic backfill for pre-existing rows, BEFORE the unique indexes below are
            // created — mirrors what SlugGenerator.Generate() + the Create-handler collision-retry
            // would produce for a fresh row: "{type}-{locality}-{short id suffix}", lowercased and
            // hyphenated. Locality prefers City, falls back to District (City is optional on both
            // listing types). The ROW_NUMBER/rn suffix is a belt-and-braces dedup step — the 8-char
            // id prefix alone is already astronomically unlikely to collide within one table — so the
            // unique index below can never fail to apply against pre-existing data.
            migrationBuilder.Sql("""
                WITH base AS (
                    SELECT rl."Id" AS "Id",
                           lower(regexp_replace(COALESCE(rt."Name", 'room'), '[^a-zA-Z0-9]+', '-', 'g'))
                             || '-' ||
                           lower(regexp_replace(COALESCE(c."Name", d."Name", 'india'), '[^a-zA-Z0-9]+', '-', 'g'))
                             || '-' ||
                           lower(substring(rl."Id"::text, 1, 8)) AS base_slug
                    FROM "RoomListings" rl
                    LEFT JOIN "RoomTypes" rt ON rt."Id" = rl."RoomTypeId"
                    LEFT JOIN "Districts" d ON d."Id" = rl."DistrictId"
                    LEFT JOIN "Cities" c ON c."Id" = rl."CityId"
                ),
                ranked AS (
                    SELECT "Id", base_slug,
                           ROW_NUMBER() OVER (PARTITION BY base_slug ORDER BY "Id") AS rn
                    FROM base
                )
                UPDATE "RoomListings" rl
                SET "Slug" = CASE WHEN ranked.rn = 1 THEN ranked.base_slug ELSE ranked.base_slug || '-' || ranked.rn::text END
                FROM ranked
                WHERE ranked."Id" = rl."Id";
                """);

            migrationBuilder.Sql("""
                WITH base AS (
                    SELECT pl."Id" AS "Id",
                           lower(regexp_replace(COALESCE(pt."Name", 'plot'), '[^a-zA-Z0-9]+', '-', 'g'))
                             || '-' ||
                           lower(regexp_replace(COALESCE(c."Name", d."Name", 'india'), '[^a-zA-Z0-9]+', '-', 'g'))
                             || '-' ||
                           lower(substring(pl."Id"::text, 1, 8)) AS base_slug
                    FROM "PlotListings" pl
                    LEFT JOIN "PlotTypes" pt ON pt."Id" = pl."PlotTypeId"
                    LEFT JOIN "Districts" d ON d."Id" = pl."DistrictId"
                    LEFT JOIN "Cities" c ON c."Id" = pl."CityId"
                ),
                ranked AS (
                    SELECT "Id", base_slug,
                           ROW_NUMBER() OVER (PARTITION BY base_slug ORDER BY "Id") AS rn
                    FROM base
                )
                UPDATE "PlotListings" pl
                SET "Slug" = CASE WHEN ranked.rn = 1 THEN ranked.base_slug ELSE ranked.base_slug || '-' || ranked.rn::text END
                FROM ranked
                WHERE ranked."Id" = pl."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RoomListings_Slug",
                table: "RoomListings",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlotListings_Slug",
                table: "PlotListings",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoomListings_Slug",
                table: "RoomListings");

            migrationBuilder.DropIndex(
                name: "IX_PlotListings_Slug",
                table: "PlotListings");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "RoomListings");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "PlotListings");
        }
    }
}
