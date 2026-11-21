using System.Collections.Concurrent;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #696: the Windows keep-awake request belongs to one thread, and it holds while any
/// device streams.
/// </summary>
/// <remarks>
/// <para>
/// <c>SetThreadExecutionState</c> sets the state of the calling thread, and a thread has one
/// state. Two consequences shaped <see cref="KeepAwakeOwner"/>:
/// </para>
/// <list type="number">
/// <item>A release must run on the thread that set the state. Core reports a drop on its own
/// thread, not on the thread that started the stream.</item>
/// <item>The state is not counted. With two devices streaming, one call that clears it lets the
/// computer sleep while the other device still streams.</item>
/// </list>
/// <para>
/// The tests give the owner a recording function in place of the Win32 call, so they run on any
/// platform.
/// </para>
/// </remarks>
public sealed class KeepAwakeOwnerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<(bool KeepAwake, int ThreadId)> _applied = new();
    private readonly KeepAwakeOwner _owner;

    public KeepAwakeOwnerTests()
    {
        _owner = new KeepAwakeOwner(keepAwake =>
        {
            _applied.Add((keepAwake, Environment.CurrentManagedThreadId));
            return true;
        });
    }

    public void Dispose() => _owner.Dispose();

    /// <summary>
    /// Two devices stream and both stop. The state is set when the first starts and cleared when
    /// the last stops, and at no time between.
    /// </summary>
    [Fact]
    public void The_computer_stays_awake_until_the_last_stream_ends()
    {
        _owner.Acquire();
        _owner.Acquire();
        _owner.Release();
        _owner.Release();

        Assert.Equal([true, false], AppliedUntilCleared().Select(call => call.KeepAwake));
    }

    /// <summary>
    /// A stream starts on one thread and ends on another, as it does when Core reports a drop. The
    /// state is set and cleared on one thread all the same.
    /// </summary>
    [Fact]
    public void The_state_is_set_and_cleared_on_one_thread_whichever_thread_asks()
    {
        _owner.Acquire();

        // This thread stays alive while the other releases, so the two cannot share an id.
        var releaser = new Thread(_owner.Release);
        releaser.Start();
        Assert.True(releaser.Join(Timeout));

        var calls = AppliedUntilCleared();
        Assert.Equal([true, false], calls.Select(call => call.KeepAwake));
        Assert.Equal(calls[0].ThreadId, calls[1].ThreadId);
    }

    /// <summary>
    /// A release with no stream to end changes nothing. It must not make the count go below zero,
    /// or the next stream would not keep the computer awake.
    /// </summary>
    [Fact]
    public void A_release_with_nothing_held_does_not_cancel_the_next_stream()
    {
        _owner.Release();
        _owner.Acquire();
        _owner.Release();

        Assert.Equal([true, false], AppliedUntilCleared().Select(call => call.KeepAwake));
    }

    /// <summary>
    /// The calls the owner made, up to and including the first that clears the state. Calls run
    /// on the owner's thread, so this waits for them.
    /// </summary>
    private List<(bool KeepAwake, int ThreadId)> AppliedUntilCleared()
    {
        var calls = new List<(bool KeepAwake, int ThreadId)>();
        while (true)
        {
            Assert.True(
                _applied.TryTake(out var call, Timeout),
                $"The owner did not clear the state within {Timeout.TotalSeconds} s. Calls so far: " +
                string.Join(", ", calls.Select(c => c.KeepAwake)));
            calls.Add(call);

            if (!call.KeepAwake)
            {
                return calls;
            }
        }
    }
}
