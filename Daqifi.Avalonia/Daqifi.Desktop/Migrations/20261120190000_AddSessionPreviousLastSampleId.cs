// Downstream addition (#514) — no upstream counterpart, so no `// @port:` marker.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daqifi.Desktop.Migrations;

/// <summary>
/// Adds <c>Sessions.PreviousLastSampleId</c>: for a session that the import of earlier sessions
/// (<see cref="Daqifi.Desktop.Logger.PreviousSessionImport"/>) copied, the ID of the last old
/// sample the copy holds, in the earlier version's database. Null for every other session.
/// </summary>
/// <remarks>
/// Nothing in the earlier version's database proves that a session has ended, so the import can
/// copy a session that is still being recorded. With this ID it can see later that the old session
/// has samples after the last one copied, offer them again, and copy only those. A sample ID is
/// never given twice by a table with AUTOINCREMENT, and a new one is always higher, so every old
/// sample at or below this ID is in the copy.
/// Nullable with no default, so every row already in the database reads as "nothing to compare",
/// which is correct for every released version: no release made copies.
/// </remarks>
public partial class AddSessionPreviousLastSampleId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "PreviousLastSampleId",
            table: "Sessions",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PreviousLastSampleId",
            table: "Sessions");
    }
}
