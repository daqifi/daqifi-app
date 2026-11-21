using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// #682 review: the purge and the imports claim one gate. Imports may overlap each other; the purge
/// needs the store to itself; a claim is released once, when it is disposed.
/// </summary>
public class SessionStoreGateTests
{
    [Fact]
    public void APurge_CannotStartWhileAnImportHoldsAClaim_AndCanOnceItEnds()
    {
        var gate = new SessionStoreGate();
        var import = gate.TryBeginImport();
        Assert.NotNull(import);
        Assert.True(gate.IsImportRunning);

        Assert.Null(gate.TryBeginPurge());

        import.Dispose();
        Assert.False(gate.IsImportRunning);
        using var purge = gate.TryBeginPurge();
        Assert.NotNull(purge);
    }

    [Fact]
    public void AnImport_CannotStartWhileThePurgeHoldsTheStore_AndCanOnceItEnds()
    {
        var gate = new SessionStoreGate();
        var purge = gate.TryBeginPurge();
        Assert.NotNull(purge);

        Assert.Null(gate.TryBeginImport());
        Assert.Null(gate.TryBeginPurge());
        Assert.False(gate.IsImportRunning);

        purge.Dispose();
        using var import = gate.TryBeginImport();
        Assert.NotNull(import);
    }

    [Fact]
    public void Imports_MayOverlap_AndTheStoreIsFreeOnlyWhenTheLastEnds()
    {
        var gate = new SessionStoreGate();
        var first = gate.TryBeginImport();
        var second = gate.TryBeginImport();
        Assert.NotNull(first);
        Assert.NotNull(second);

        first.Dispose();
        first.Dispose(); // released once, however many times it is disposed
        Assert.True(gate.IsImportRunning);
        Assert.Null(gate.TryBeginPurge());

        second.Dispose();
        using var purge = gate.TryBeginPurge();
        Assert.NotNull(purge);
    }
}
