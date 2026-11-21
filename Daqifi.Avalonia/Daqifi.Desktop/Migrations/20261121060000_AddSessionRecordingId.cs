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
/// unique index, and a trigger: a row inserted without the column (an older build on this file does
/// not know it) gets an identity from the database itself, so every row has one (#693 review). The
/// column is nullable, and the trigger is AFTER INSERT, because SQLite cannot add a NOT NULL column
/// without a constant default, and a column default cannot call randomblob().</para>
/// <para>Every path that builds the schema runs the migrations, so every path gets the trigger: a new
/// database, the corruption quarantine and DELETE ALL's empty file. A later migration that makes EF
/// rebuild the <c>Sessions</c> table drops its triggers, so it must create this one again; the test
/// that looks for it on a freshly migrated database fails until it does.</para>
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

        migrationBuilder.Sql($"UPDATE Sessions SET RecordingId = {NewIdentitySql} WHERE RecordingId IS NULL;");

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_RecordingId",
            table: "Sessions",
            column: "RecordingId",
            unique: true);

        migrationBuilder.Sql(
            $"CREATE TRIGGER {IdentityTrigger} AFTER INSERT ON Sessions " +
            "FOR EACH ROW WHEN NEW.RecordingId IS NULL " +
            $"BEGIN UPDATE Sessions SET RecordingId = {NewIdentitySql} WHERE ID = NEW.ID; END;");
    }

    /// <summary>The trigger that gives a row inserted without an identity one of its own.</summary>
    internal const string IdentityTrigger = "TR_Sessions_RecordingId";

    /// <summary>
    /// A new random GUID in the text form EF writes one in (upper case, with dashes). randomblob()
    /// runs again for each row, so each row gets its own value.
    /// </summary>
    private const string NewIdentitySql =
        "(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' || " +
        "hex(randomblob(2)) || '-' || hex(randomblob(6)))";

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"DROP TRIGGER IF EXISTS {IdentityTrigger};");

        migrationBuilder.DropIndex(
            name: "IX_Sessions_RecordingId",
            table: "Sessions");

        migrationBuilder.DropColumn(
            name: "RecordingId",
            table: "Sessions");
    }
}
