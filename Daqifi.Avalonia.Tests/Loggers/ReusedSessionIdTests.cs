using System.Collections.ObjectModel;
using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #680: the app gives a deleted session's ID to the next session (the next ID is the highest ID in
/// the database plus 1, and DELETE ALL starts again at 0). A new session with that ID must be a
/// different item from the deleted session, in every comparison the app and Avalonia make.
/// </summary>
/// <remarks>
/// <para>When the session list gets the new session, its <c>ListBox</c> gives it a row container
/// that held the deleted session. Avalonia compares the container's old and new <c>DataContext</c>
/// with <see cref="object.Equals(object?)"/>, and it does not tell the row's bindings about a change
/// to an equal value. With an <c>Equals</c> that compared only the ID, the row kept the deleted
/// session's name, start and sample count, and its DELETE button kept the deleted session as its
/// parameter. That DELETE then asked about the deleted session and deleted the new one.</para>
/// <para>This test project builds no Avalonia control (see its csproj), so the realized row is
/// checked by <c>HeadlessBench --scripted session-id-reuse</c> (row LOGGED-DEL), which CI runs.
/// These tests hold the comparisons that the row and the list depend on.</para>
/// </remarks>
public class ReusedSessionIdTests
{
    /// <summary>The comparison the row container makes when it gets the new session.</summary>
    [Fact]
    public void ANewSessionWithADeletedSessionsId_IsNotEqualToTheDeletedSession()
    {
        var deleted = new LoggingSession(2, "Old two");
        var reused = new LoggingSession(2, "Session_2");

        Assert.False(deleted.Equals(reused));
        Assert.False(reused.Equals(deleted));
    }

    /// <summary>
    /// The list removal the row's DELETE makes after the database delete. With the deleted session as
    /// the parameter, <c>Remove</c> found the new session (equal by ID) and removed that row.
    /// </summary>
    [Fact]
    public void RemovingTheDeletedSession_DoesNotRemoveTheNewSessionWithItsId()
    {
        var oldZero = new LoggingSession(0, "Old zero");
        var oldOne = new LoggingSession(1, "Old one");
        var deleted = new LoggingSession(2, "Old two");
        var reused = new LoggingSession(2, "Session_2");
        var list = new ObservableCollection<LoggingSession> { oldZero, oldOne, reused };

        Assert.False(list.Remove(deleted));
        Assert.Equal(-1, list.IndexOf(deleted));
        Assert.Equal([oldZero, oldOne, reused], list);
    }

    /// <summary>The control: each session is still equal to itself, so a list finds and removes it.</summary>
    [Fact]
    public void ASession_IsEqualToItself_SoTheListRemovesIt()
    {
        var session = new LoggingSession(2, "Session_2");
        var list = new ObservableCollection<LoggingSession> { new(0, "Old zero"), session };

        Assert.True(session.Equals(session));
        Assert.True(list.Remove(session));
        Assert.DoesNotContain(session, list);
    }
}
