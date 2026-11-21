// Downstream addition (#680, #693 review) — no upstream counterpart, so no `// @port:` marker.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daqifi.Desktop.Migrations;

/// <summary>
/// Adds <c>Sessions.RecordingId</c>: a GUID that identifies a recording, given once when its row is
/// created (<see cref="Daqifi.Desktop.Logger.LoggingSession.RecordingId"/>). A delete compares it in
/// its transaction, because the session ID is used again for a new recording, and the start is not
/// unique either (two boards can log files with the same name, and the SD importer gives them the
/// same start).
/// </summary>
/// <remarks>
/// <para>Every existing row gets a fresh random GUID here, in the same text form EF writes. Then a
/// unique index. The column is nullable because SQLite cannot add a NOT NULL column without a constant
/// default, and a constant would break the unique index for any writer that does not know the
/// column (an older build on this file). Such a row stays null; a delete then cannot tell it from a
/// replacement and falls back to the ID, as before this column.</para>
/// <para>DELETE ALL and the corruption quarantine build a new, empty file: there is no row to keep an
/// identity for, so neither needs to carry anything across.</para>
/// </remarks>
public partial class AddSessionRecordingId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "RecordingId",
            table: "Sessions",
            type: "TEXT",
            nullable: true);

        // randomblob() runs once for each row, so each row gets its own value. hex() gives upper
        // case, which is the form EF writes a Guid in.
        migrationBuilder.Sql(
            "UPDATE Sessions SET RecordingId = " +
            "hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || " +
            "hex(randomblob(2)) || '-' || hex(randomblob(6)) " +
            "WHERE RecordingId IS NULL;");

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_RecordingId",
            table: "Sessions",
            column: "RecordingId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Sessions_RecordingId",
            table: "Sessions");

        migrationBuilder.DropColumn(
            name: "RecordingId",
            table: "Sessions");
    }
}
