using System.Text.Json;
using Daqifi.Desktop.UpdateVersion;
using Xunit;

namespace Daqifi.Avalonia.Tests.UpdateVersion;

/// <summary>
/// Which releases' notes the UPDATES section shows (#347): every release after the installed
/// version up to the pending one, newest first, so a user who skipped versions reads what each of
/// them changed. Prereleases are in the range only for a copy that follows prereleases, as the
/// update channel does. Built from canned releases API answers; no test touches the network.
/// </summary>
public class GitHubReleaseNotesTests
{
    /// <summary>A releases API answer, in the order given (the API lists newest first).</summary>
    internal static string Answer(params (string Tag, bool Prerelease, string? Body)[] releases) =>
        Answer(releases.Select(r => (r.Tag, r.Prerelease, false, r.Body)).ToArray());

    internal static string Answer(params (string Tag, bool Prerelease, bool Draft, string? Body)[] releases) =>
        JsonSerializer.Serialize(releases.Select(r => new Dictionary<string, object?>
        {
            ["tag_name"] = r.Tag,
            ["name"] = $"{r.Tag} — a release",
            ["prerelease"] = r.Prerelease,
            ["draft"] = r.Draft,
            ["body"] = r.Body,
            ["html_url"] = $"https://github.com/daqifi/daqifi-app/releases/tag/{r.Tag}",
            ["assets"] = Array.Empty<object>(),
        }));

    private static PublishedRelease Release(string version, bool prerelease = false) =>
        new(version, prerelease, $"Notes of {version}", $"https://github.com/daqifi/daqifi-app/releases/tag/v{version}");

    private static string[] Versions(IEnumerable<PublishedRelease> releases) => [.. releases.Select(r => r.Version)];

    [Fact]
    public void Parse_reads_the_version_flag_body_and_page_of_each_release()
    {
        var releases = GitHubReleaseNotes.Parse(Answer(("v3.4.0", false, "## Fixes"), ("v3.4.0-beta.1", true, null)));

        Assert.Equal(
        [
            new PublishedRelease("3.4.0", false, "## Fixes", "https://github.com/daqifi/daqifi-app/releases/tag/v3.4.0"),
            new PublishedRelease("3.4.0-beta.1", true, null, "https://github.com/daqifi/daqifi-app/releases/tag/v3.4.0-beta.1"),
        ], releases);
    }

    /// <summary>The updater cannot see a draft, so neither can its notes; a tag that is no version is not a release of the app.</summary>
    [Fact]
    public void Parse_leaves_out_drafts_and_tags_that_are_not_versions()
    {
        var releases = GitHubReleaseNotes.Parse(Answer(
            ("v3.5.0", false, true, "draft"),
            ("nightly", false, false, "not a version"),
            ("v3.4.0", false, false, "real")));

        Assert.Equal(["3.4.0"], Versions(releases));
    }

    // ---- The version range ----

    /// <summary>The user the feature is for: installed 3.3.0, next opened at 3.6.0. 3.4.0 and 3.5.0 are not dropped.</summary>
    [Fact]
    public void Skipped_versions_give_every_release_in_between_newest_first()
    {
        var releases = new[] { Release("3.4.0"), Release("3.6.0"), Release("3.3.0"), Release("3.5.0"), Release("3.2.0") };

        Assert.Equal(["3.6.0", "3.5.0", "3.4.0"], Versions(GitHubReleaseNotes.Between(releases, "3.3.0", "3.6.0")));
    }

    /// <summary>The installed version's notes are old news, and a release published after the check is not the one on offer.</summary>
    [Fact]
    public void The_range_leaves_out_the_installed_version_and_anything_after_the_pending_one()
    {
        var releases = new[] { Release("3.7.0"), Release("3.6.0"), Release("3.5.0"), Release("3.3.0") };

        Assert.Equal(["3.6.0", "3.5.0"], Versions(GitHubReleaseNotes.Between(releases, "3.3.0", "3.6.0")));
    }

    /// <summary>A stable copy follows only stable releases (the update service's channel), so betas are not its history.</summary>
    [Fact]
    public void A_stable_copy_gets_no_prerelease_notes()
    {
        var releases = new[] { Release("3.4.0"), Release("3.4.0-beta.2", true), Release("3.4.0-beta.1", true), Release("3.3.1") };

        Assert.Equal(["3.4.0", "3.3.1"], Versions(GitHubReleaseNotes.Between(releases, "3.3.0", "3.4.0")));
    }

    /// <summary>A beta copy follows prereleases. beta.10 comes after beta.9: the numbers compare as numbers.</summary>
    [Fact]
    public void A_prerelease_copy_gets_the_prereleases_in_version_order()
    {
        var releases = new[]
        {
            Release("3.3.0-beta.11", true), Release("3.3.0-beta.10", true), Release("3.3.0-beta.9", true),
            Release("3.3.0-beta.2", true),
        };

        Assert.Equal(
            ["3.3.0-beta.11", "3.3.0-beta.10"],
            Versions(GitHubReleaseNotes.Between(releases, "3.3.0-beta.9", "3.3.0-beta.11")));
    }

    /// <summary>A beta copy offered the final release gets the final release and the betas it skipped.</summary>
    [Fact]
    public void A_prerelease_copy_offered_the_final_release_gets_it_and_the_betas_between()
    {
        var releases = new[] { Release("3.3.0"), Release("3.3.0-beta.3", true), Release("3.3.0-beta.2", true) };

        Assert.Equal(["3.3.0", "3.3.0-beta.3"], Versions(GitHubReleaseNotes.Between(releases, "3.3.0-beta.2", "3.3.0")));
    }

    /// <summary>The updater writes the version without the tag's "v", and with any build metadata the install carries.</summary>
    [Fact]
    public void Versions_match_with_or_without_the_v_and_build_metadata()
    {
        var releases = GitHubReleaseNotes.Parse(Answer(("v3.4.0", false, "new"), ("V3.3.0", false, "old")));

        Assert.Equal(["3.4.0"], Versions(GitHubReleaseNotes.Between(releases, "3.3.0+9f1c2ab", "v3.4.0")));
    }

    /// <summary>Without a version for this copy, only the pending release is certain to be new.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("dev")]
    public void An_unknown_installed_version_gives_only_the_pending_release(string? installed)
    {
        var releases = new[] { Release("3.4.0"), Release("3.3.0") };

        Assert.Equal(["3.4.0"], Versions(GitHubReleaseNotes.Between(releases, installed, "3.4.0")));
    }

    // ---- What the section shows ----

    [Fact]
    public void Sections_put_each_release_under_its_version_newest_first()
    {
        var json = Answer(("v3.5.0", false, "## Added\n- Export"), ("v3.4.0", false, "Fixes."), ("v3.3.0", false, "Old."));

        var sections = GitHubReleaseNotes.Sections(json, "3.3.0", "3.5.0");

        Assert.Equal(["3.5.0", "3.4.0"], sections.Select(s => s.Version));
        Assert.Equal(
            ["Added", "Export"],
            sections[0].Blocks.Select(b => string.Concat(b.Spans.Select(s => s.Text))));
        Assert.Equal("Fixes.", Assert.Single(Assert.Single(sections[1].Blocks).Spans).Text);
    }

    [Fact]
    public void Unavailable_says_so_and_links_the_release_page()
    {
        var section = Assert.Single(GitHubReleaseNotes.Unavailable("3.4.0"));

        Assert.Equal("3.4.0", section.Version);
        var spans = Assert.Single(section.Blocks).Spans;
        Assert.StartsWith("Release notes unavailable.", spans[0].Text);
        Assert.Equal("https://github.com/daqifi/daqifi-app/releases/tag/v3.4.0", spans.Single(s => s.Link is not null).Link);
        Assert.Equal("https://github.com/daqifi/daqifi-app/releases/tag/v3.4.0", GitHubReleaseNotes.ReleasePage("3.4.0"));
    }

    /// <summary>A pending version the answer does not list, for example on a later page or still a draft there.</summary>
    [Fact]
    public void A_pending_version_the_answer_does_not_list_is_unavailable()
    {
        var sections = GitHubReleaseNotes.Sections(Answer(("v3.3.0", false, "Old.")), "3.3.0", "3.4.0");

        Assert.Equal(GitHubReleaseNotes.Unavailable("3.4.0").Select(s => s.Version), sections.Select(s => s.Version));
        Assert.Contains(sections[0].Blocks[0].Spans, s => s.Link == GitHubReleaseNotes.ReleasePage("3.4.0"));
    }

    /// <summary>A release without notes gets the line and its own page, and the others keep theirs.</summary>
    [Fact]
    public void A_release_with_no_body_is_unavailable_and_links_its_page()
    {
        var sections = GitHubReleaseNotes.Sections(Answer(("v3.5.0", false, "New."), ("v3.4.0", false, "  ")), "3.3.0", "3.5.0");

        Assert.Equal("New.", sections[0].Blocks[0].Spans[0].Text);
        Assert.StartsWith("Release notes unavailable.", sections[1].Blocks[0].Spans[0].Text);
        Assert.Contains(sections[1].Blocks[0].Spans, s => s.Link == "https://github.com/daqifi/daqifi-app/releases/tag/v3.4.0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    [InlineData("[{\"tag_name\": 5}]")]
    public void An_answer_that_cannot_be_read_is_unavailable(string json)
    {
        var section = Assert.Single(GitHubReleaseNotes.Sections(json, "3.3.0", "3.4.0"));

        Assert.Equal("3.4.0", section.Version);
        Assert.Contains(section.Blocks[0].Spans, s => s.Link == GitHubReleaseNotes.ReleasePage("3.4.0"));
    }
}
