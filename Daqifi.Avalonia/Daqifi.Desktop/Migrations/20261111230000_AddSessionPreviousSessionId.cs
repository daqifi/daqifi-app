// Downstream addition (#511) — no upstream counterpart, so no `// @port:` marker.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daqifi.Desktop.Migrations;

/// <summary>
/// Adds <c>Sessions.PreviousSessionId</c>: for a session that the import of earlier sessions
/// (<see cref="Daqifi.Desktop.Logger.PreviousSessionImport"/>) copied, the ID that session has in
/// the earlier version's database. Null for every other session.
/// </summary>
/// <remarks>
/// The import must know which sessions are its own copies: it skips an old session that already
/// has one, and it deletes only its own partial copies. The data cannot tell this reliably (#511):
/// two different SD card logs can start in the same second and with the same first sample, and a
/// partial copy loses its first samples while its clean-up runs. Nullable with no default, so every
/// row already in the database reads as "not a copy", which is correct for every released version:
/// no release before this column made copies.
/// </remarks>
public partial class AddSessionPreviousSessionId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "PreviousSessionId",
            table: "Sessions",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PreviousSessionId",
            table: "Sessions");
    }
}
