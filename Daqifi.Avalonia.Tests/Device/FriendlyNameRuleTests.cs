using Daqifi.Core.Communication.Producers;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #596: the device drawer showed Core's exception text, "(Parameter 'name')" included, when
/// a device name was refused. <see cref="FriendlyNameRule"/> now writes the sentence the user sees.
///
/// <para>
/// Every case is also checked against Core's own <see cref="ScpiMessageProducer.IsFriendlyNameValid"/>,
/// in both directions: the app must never call a name good that the device refuses (it would reach
/// the SCPI write), and never call a name bad that the device accepts (SAVE NAME would stay
/// disabled for no reason).
/// </para>
/// </summary>
public class FriendlyNameRuleTests
{
    public static TheoryData<string, string> RefusedNames => new()
    {
        { "empty", "" },
        { "emoji", "Lab \U0001F52C" },
        { "double quote", "Lab \"A\"" },
        { "backslash", "a\\b" },
        { "accented letter", "Café" },
        { "tab", "Lab\tA" },
        { "too long", new string('x', ScpiMessageProducer.MaxFriendlyNameLength + 1) },
    };

    public static TheoryData<string> AcceptedNames => new()
    {
        "Lab Nyquist",
        "x",
        new string('x', ScpiMessageProducer.MaxFriendlyNameLength),
        "Rig #1 (bench) - it's ok!",
    };

    [Theory]
    [MemberData(nameof(RefusedNames))]
    public void A_refused_name_gets_a_message_for_people(string label, string name)
    {
        var message = FriendlyNameRule.Describe(name);

        Assert.False(ScpiMessageProducer.IsFriendlyNameValid(name), $"Core accepts the '{label}' case");
        Assert.NotNull(message);
        Assert.DoesNotContain("Parameter", message);
        Assert.DoesNotContain("ASCII", message);
    }

    [Theory]
    [MemberData(nameof(AcceptedNames))]
    public void An_accepted_name_gets_no_message(string name)
    {
        Assert.True(ScpiMessageProducer.IsFriendlyNameValid(name));
        Assert.Null(FriendlyNameRule.Describe(name));
    }

    [Fact]
    public void Null_is_refused_like_an_empty_name()
    {
        Assert.Equal(FriendlyNameRule.Describe(""), FriendlyNameRule.Describe(null));
    }

    /// <summary>The messages state the real limit, which is Core's, not a copy of it.</summary>
    [Fact]
    public void The_length_messages_state_cores_limit()
    {
        var max = ScpiMessageProducer.MaxFriendlyNameLength;

        Assert.Equal(max, FriendlyNameRule.MaxLength);
        Assert.Contains($"1 to {max}", FriendlyNameRule.Describe(""));
        Assert.Contains($"{max} characters or fewer", FriendlyNameRule.Describe(new string('x', max + 1)));
        Assert.Contains($"1 to {max}", FriendlyNameRule.RuleText);
    }

    [Fact]
    public void A_character_the_user_can_see_is_named()
    {
        Assert.Contains("'é'", FriendlyNameRule.Describe("Café"));
        Assert.Contains("'\U0001F52C'", FriendlyNameRule.Describe("Lab \U0001F52C"));
    }

    [Fact]
    public void A_character_the_user_cannot_see_is_described_not_shown()
    {
        var message = FriendlyNameRule.Describe("Lab\u0001A");

        Assert.Contains("hidden control character", message);
        // Ordinal, by char: a culture-aware substring search treats U+0001 as ignorable and
        // "finds" it at position 0 of any string.
        Assert.NotNull(message);
        Assert.DoesNotContain('\u0001', message);
    }

    /// <summary>
    /// A bad character is reported before the length, because it is what the user must change.
    /// Thirty letters and one emoji are 32 UTF-16 units: "too long" would send them off to delete
    /// letters that were fine.
    /// </summary>
    [Fact]
    public void A_bad_character_is_reported_before_the_length()
    {
        var name = new string('x', ScpiMessageProducer.MaxFriendlyNameLength - 1) + "\U0001F52C";

        Assert.True(name.Length > ScpiMessageProducer.MaxFriendlyNameLength);
        Assert.Contains("emoji", FriendlyNameRule.Describe(name));
    }
}
