using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Models;
using Xunit;
using CoreSdCardFileInfo = Daqifi.Core.Device.SdCard.SdCardFileInfo;

namespace Daqifi.Avalonia.Tests.Models;

/// <summary>
/// How big each SD card log is, and why the listing has to say (issue #521).
///
/// <para>
/// The device reports a size per entry in <c>SYSTem:STORage:SD:LIST?</c> and Core parses it into
/// <c>SdCardFileInfo.SizeInBytes</c>; this app read the name and the date off each entry and threw
/// the size away. Two of the five logs on the test card were zero bytes, which cannot import — and
/// with name, date and format the only things on the row, nothing distinguished one of those from a
/// log whose transfer had failed. That is what made the silent failure in #521 baffling rather than
/// merely annoying.
/// </para>
///
/// <para>
/// The distinction these are really about is 0 versus unknown. Core leaves the size unset when a
/// listing entry carries no size token rather than guessing zero, precisely because a real 0 is
/// meaningful, and a listing that asserted "Empty" on the strength of a missing token would send a
/// user looking for a fault in a file that is fine.
/// </para>
/// </summary>
public class SdCardFileSizeTests
{
    /// <summary>
    /// The one a user has to be able to read off the row without converting anything: a log the
    /// device wrote nothing into says so in words.
    /// </summary>
    [Fact]
    public void A_zero_byte_log_reads_as_empty_and_is_flagged()
    {
        var file = new SdCardFile { FileName = "LOG_0001.bin", SizeInBytes = 0 };

        Assert.Equal("Empty", file.SizeDisplay);
        Assert.True(file.IsEmpty);
    }

    /// <summary>
    /// A listing entry with no size token is not an empty file. Reporting it as one would have the
    /// row assert a fact about the log that this app never learned.
    /// </summary>
    [Fact]
    public void A_missing_size_is_reported_as_unknown_not_as_empty()
    {
        var file = new SdCardFile { FileName = "LOG_0001.bin", SizeInBytes = null };

        Assert.Equal("Size unknown", file.SizeDisplay);
        Assert.False(file.IsEmpty);
    }

    [Theory]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(9112 * 20, "178 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData(5L * 1024 * 1024 + 512 * 1024, "5.5 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData(3L * 1024 * 1024 * 1024 / 2, "1.5 GB")]
    public void A_size_is_scaled_to_the_unit_a_user_reads(long bytes, string expected) =>
        Assert.Equal(expected, new SdCardFile { FileName = "LOG_0001.bin", SizeInBytes = bytes }.SizeDisplay);

    /// <summary>
    /// And the size survives the mapping out of Core, which is where it was being dropped.
    /// Asserted on the mapper rather than on the model alone, because the model always could have
    /// held a size; what #521 was about is <c>AbstractStreamingDevice</c> reading the name and the
    /// date off each listing entry and discarding the third field.
    /// </summary>
    [Fact]
    public void The_size_core_reported_reaches_the_listing()
    {
        var mapped = AbstractStreamingDevice.MapSdCardFiles([
            new CoreSdCardFileInfo("LOG_0001.bin", DateTime.MinValue, sizeInBytes: 0),
            new CoreSdCardFileInfo("LOG_0002.bin", DateTime.MinValue, sizeInBytes: 182_240),
            new CoreSdCardFileInfo("LOG_0003.bin", DateTime.MinValue, sizeInBytes: null),
        ]);

        Assert.Equal([0, 182_240, null], mapped.Select(f => f.SizeInBytes));
        Assert.Equal(["Empty", "178 KB", "Size unknown"], mapped.Select(f => f.SizeDisplay));
    }
}
