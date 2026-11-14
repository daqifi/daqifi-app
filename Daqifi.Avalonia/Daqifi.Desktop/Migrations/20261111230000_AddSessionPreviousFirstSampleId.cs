// Downstream addition (#511) — no upstream counterpart, so no `// @port:` marker.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daqifi.Desktop.Migrations;

/// <summary>
/// Adds <c>Sessions.PreviousFirstSampleId</c>: for a session that the import of earlier sessions
/// (<see cref="Daqifi.Desktop.Logger.PreviousSessionImport"/>) copied, the ID of the old session's
/// first sample in the earlier version's database. Null for every other session.
/// </summary>
/// <remarks>
/// The import must know which sessions are its own copies, and of which old session: it skips an
/// old session that already has one, and it deletes only its own partial copies. The data cannot
/// tell this reliably (#511): two different SD card logs can start in the same second and with the
/// same first sample, and a partial copy loses its first samples while its clean-up runs. The old
/// session's ID and start are not enough either: the earlier version gives a new session the
/// maximum ID plus one and takes the start from the log's file name, so a different log can get
/// both again. A sample ID is never given twice by a table with AUTOINCREMENT, so a recreated old
/// session always has a different first sample ID.
/// Nullable with no default, so every row already in the database reads as "not a copy", which is
/// correct for every released version: no release before this column made copies.
/// </remarks>
public partial class AddSessionPreviousFirstSampleId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "PreviousFirstSampleId",
            table: "Sessions",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PreviousFirstSampleId",
            table: "Sessions");
    }
}
