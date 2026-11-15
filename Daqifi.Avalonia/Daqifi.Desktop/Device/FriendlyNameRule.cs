using System.Text;
using Daqifi.Core.Communication.Producers;

namespace Daqifi.Desktop.Device;

/// <summary>
/// Tells a user, in words the app owns, why a device name cannot be used (issue #596).
/// </summary>
/// <remarks>
/// <para>
/// Core owns the rule itself (<see cref="ScpiMessageProducer.IsFriendlyNameValid"/>, a mirror of
/// firmware's <c>daqifi_settings_FriendlyNameIsValid</c>): 1 to
/// <see cref="ScpiMessageProducer.MaxFriendlyNameLength"/> printable ASCII characters, without
/// <c>"</c> or <c>\</c>. What Core does not own is the sentence a user reads. Its only way to say
/// "no" is the <see cref="ArgumentException"/> from <see cref="ScpiMessageProducer.SetDeviceName"/>,
/// whose <see cref="Exception.Message"/> carries the .NET suffix <c>(Parameter 'name')</c>, and the
/// device drawer used to show that text verbatim.
/// </para>
/// <para>
/// The checks below only choose which sentence to show. The final word is always Core's: a name
/// these checks pass but Core refuses still gets <see cref="RuleText"/>, so a future change to
/// Core's rule can make the message vaguer but never let an invalid name through.
/// </para>
/// </remarks>
public static class FriendlyNameRule
{
    /// <summary>Longest name the device accepts, in characters. Core's limit, re-exposed so the
    /// drawer's NAME box can cap its length with the same number.</summary>
    public static int MaxLength => ScpiMessageProducer.MaxFriendlyNameLength;

    /// <summary>The whole rule in one sentence, for the case no narrower message fits.</summary>
    public static string RuleText =>
        $"A device name must have 1 to {MaxLength} characters: the letters A to Z, digits, spaces " +
        "and standard keyboard symbols, but not \" or \\.";

    /// <summary>
    /// Returns a message that says what is wrong with <paramref name="name"/> and what is allowed,
    /// or null when the device will accept it.
    /// </summary>
    /// <param name="name">The name exactly as it will be sent. The drawer trims it first.</param>
    public static string? Describe(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return $"Enter a name of 1 to {MaxLength} characters.";
        }

        // Characters before length: a 31-character name with one emoji in it is too long in UTF-16
        // units, but the emoji is the thing the user has to change.
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value is '"' or '\\')
            {
                return "A device name cannot contain \" or \\.";
            }

            if (rune.Value is < 0x20 or > 0x7E)
            {
                var shown = Rune.IsControl(rune) ? "a hidden control character" : $"'{rune}'";
                return $"The name contains {shown}. Use only the letters A to Z, digits, spaces " +
                       "and standard keyboard symbols. Accented letters and emoji are not allowed.";
            }
        }

        if (name.Length > MaxLength)
        {
            return $"The name is too long. Use {MaxLength} characters or fewer.";
        }

        return ScpiMessageProducer.IsFriendlyNameValid(name) ? null : RuleText;
    }
}
