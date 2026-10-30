using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daqifi.Avalonia.Services;
using Xunit;

namespace Daqifi.Avalonia.Tests.Services;

/// <summary>
/// The .ico reading and frame choice behind the Windows title-bar glyph (#492). The Win32 calls
/// themselves are checked by eye on Windows; these pin the parts that decide which bytes reach them.
/// </summary>
public class WindowsTitleBarIconTests
{
    [Fact]
    public void Parse_ReadsEachFrameSizeAndBytes()
    {
        var ico = BuildIco((16, new byte[] { 1, 2, 3 }), (32, new byte[] { 4, 5 }), (256, new byte[] { 6 }));

        var frames = IcoFrames.Parse(ico);

        Assert.Equal(new[] { 16, 32, 256 }, frames.Select(f => f.Size));
        Assert.Equal(new byte[] { 1, 2, 3 }, frames[0].Data);
        Assert.Equal(new byte[] { 4, 5 }, frames[1].Data);
        Assert.Equal(new byte[] { 6 }, frames[2].Data);
    }

    [Fact]
    public void Parse_RejectsNonIcoHeader()
    {
        var notIco = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => IcoFrames.Parse(notIco));
    }

    [Fact]
    public void Parse_RejectsFrameRunningPastEnd()
    {
        var ico = BuildIco((16, new byte[] { 1, 2, 3 }));
        Array.Resize(ref ico, ico.Length - 1);
        Assert.Throws<InvalidDataException>(() => IcoFrames.Parse(ico));
    }

    [Theory]
    [InlineData(16, 16)]  // 100%
    [InlineData(20, 20)]  // 125%
    [InlineData(22, 24)]  // between frames: scale down, never up
    [InlineData(32, 32)]  // 200%
    [InlineData(64, 48)]  // bigger than any frame: the largest
    public void Pick_SmallestFrameAtLeastTarget(int px, int expected)
    {
        var frames = new[] { 48, 16, 32, 24, 20, 40 }.Select(s => new IcoFrame(s, Array.Empty<byte>())).ToList();
        Assert.Equal(expected, IcoFrames.Pick(frames, px).Size);
    }

    [Fact]
    public void ShippedIcon_HasEveryTitleBarSizeFrom100To300Percent()
    {
        var frames = IcoFrames.Parse(File.ReadAllBytes(FindShippedIcon()));

        Assert.Equal(new[] { 16, 20, 24, 32, 40, 48 }, frames.Select(f => f.Size).OrderBy(s => s));
        // PNG frames, which CreateIconFromResourceEx accepts directly.
        Assert.All(frames, f => Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, f.Data.Take(4)));
    }

    private static string FindShippedIcon()
    {
        var relative = Path.Combine("Daqifi.Avalonia", "Daqifi.Desktop", "Images", "AppIconSmall.ico");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) { return candidate; }
        }
        throw new FileNotFoundException($"{relative} not found above {AppContext.BaseDirectory}.");
    }

    private static byte[] BuildIco(params (int Size, byte[] Data)[] frames)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((ushort)0));
        bytes.AddRange(BitConverter.GetBytes((ushort)1));
        bytes.AddRange(BitConverter.GetBytes((ushort)frames.Length));
        var offset = 6 + 16 * frames.Length;
        foreach (var (size, data) in frames)
        {
            bytes.Add((byte)(size >= 256 ? 0 : size));
            bytes.Add((byte)(size >= 256 ? 0 : size));
            bytes.Add(0);
            bytes.Add(0);
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes((ushort)32));
            bytes.AddRange(BitConverter.GetBytes((uint)data.Length));
            bytes.AddRange(BitConverter.GetBytes((uint)offset));
            offset += data.Length;
        }
        foreach (var (_, data) in frames) { bytes.AddRange(data); }
        return bytes.ToArray();
    }
}
