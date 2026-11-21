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

    // ---- PR #695 review: every page up to the installed version ----

    private static Uri PageUri(int number) => number == 1
        ? GitHubReleaseNotes.FirstPage
        : new Uri($"https://api.github.com/repositories/1/releases?per_page=100&page={number}");

    /// <summary>A releases API of several pages: each page links the next, but the last. Records each request.</summary>
    private sealed class Pages(params string[] answers)
    {
        public List<Uri> Requested { get; } = [];

        public Task<ReleasesPage> Fetch(Uri uri, CancellationToken cancellationToken)
        {
            Requested.Add(uri);
            var number = uri == GitHubReleaseNotes.FirstPage ? 1 : int.Parse(uri.Query.Split("page=")[^1]);
            return Task.FromResult(new ReleasesPage(answers[number - 1], number < answers.Length ? PageUri(number + 1) : null));
        }
    }

    /// <summary>
    /// The releases API lists prereleases on the same pages, before any filter. A stable release in
    /// the range that falls on page two is still in the notes.
    /// </summary>
    [Fact]
    public async Task A_stable_release_on_page_two_is_in_the_notes()
    {
        var pages = new Pages(
            Answer(("v3.6.0", false, "Six."), ("v3.6.0-rc.1", true, "rc"), ("v3.5.1-beta.1", true, "beta")),
            Answer(("v3.5.0", false, "Five."), ("v3.4.0", false, "Four."), ("v3.3.0", false, "Three.")));

        var notes = await GitHubReleaseNotes.LoadAsync(pages.Fetch, "3.3.0", "3.6.0", CancellationToken.None);

        Assert.True(notes.HasPending);
        Assert.Equal(["3.6.0", "3.5.0", "3.4.0"], notes.Sections.Select(s => s.Version));
        Assert.Equal([PageUri(1), PageUri(2)], pages.Requested);
    }

    /// <summary>A user a few versions behind finds the installed version on page one: one request, even with more pages.</summary>
    [Fact]
    public async Task A_page_that_reaches_the_installed_version_is_the_last_one_read()
    {
        var pages = new Pages(
            Answer(("v3.4.0", false, "Four."), ("v3.3.0", false, "Three.")),
            Answer(("v3.2.0", false, "Two.")));

        var notes = await GitHubReleaseNotes.LoadAsync(pages.Fetch, "3.3.0", "3.4.0", CancellationToken.None);

        Assert.Equal(["3.4.0"], notes.Sections.Select(s => s.Version));
        Assert.Equal([PageUri(1)], pages.Requested);
    }

    /// <summary>
    /// Paging stops at <see cref="GitHubReleaseNotes.MaxPages"/>. The notes read are shown, and a last
    /// section says that older notes are not, with a link to every release.
    /// </summary>
    [Fact]
    public async Task Paging_stops_at_the_cap_and_says_that_older_notes_are_not_shown()
    {
        var answers = Enumerable.Range(1, GitHubReleaseNotes.MaxPages + 5)
            .Select(n => n == 1 ? Answer(("v9.0.0", false, "Nine.")) : Answer(($"v8.{100 - n}.0", false, $"Page {n}.")))
            .ToArray();
        var pages = new Pages(answers);

        var notes = await GitHubReleaseNotes.LoadAsync(pages.Fetch, "1.0.0", "9.0.0", CancellationToken.None);

        Assert.Equal(GitHubReleaseNotes.MaxPages, pages.Requested.Count);
        Assert.True(notes.HasPending);
        Assert.Equal("9.0.0", notes.Sections[0].Version);
        Assert.Equal(GitHubReleaseNotes.MaxPages, notes.Sections.Count - 1);
        var last = notes.Sections[^1];
        Assert.Equal("Earlier releases", last.Version);
        var spans = Assert.Single(last.Blocks).Spans;
        Assert.Contains(
            (GitHubReleaseNotes.MaxPages * 100).ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            string.Concat(spans.Select(s => s.Text)));
        Assert.Contains(spans, s => s.Link == GitHubReleaseNotes.AllReleasesPage);
    }

    [Theory]
    [InlineData(
        "<https://api.github.com/repositories/123/releases?per_page=100&page=2>; rel=\"next\", <https://api.github.com/repositories/123/releases?per_page=100&page=5>; rel=\"last\"",
        "https://api.github.com/repositories/123/releases?per_page=100&page=2")]
    [InlineData(
        "<https://api.github.com/repositories/123/releases?per_page=100&page=4>; rel=\"prev\", <https://api.github.com/repositories/123/releases?per_page=100&page=1>; rel=\"first\"",
        null)]
    [InlineData("<https://example.com/releases?page=2>; rel=\"next\"", null)]
    [InlineData("<http://api.github.com/repositories/123/releases?page=2>; rel=\"next\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Next_page_is_the_rel_next_address_on_the_api_host(string? linkHeader, string? expected) =>
        Assert.Equal(expected is null ? null : new Uri(expected), GitHubReleaseNotes.NextPage(linkHeader));

    /// <summary>An answer without the offered version's release is no result to keep: the next check asks again.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"tag_name\":\"v3.3.0\",\"prerelease\":false,\"draft\":false,\"body\":\"Old.\"}]")]
    public async Task An_answer_without_the_offered_release_is_not_a_result(string json)
    {
        var notes = await GitHubReleaseNotes.LoadAsync(new Pages(json).Fetch, "3.3.0", "3.4.0", CancellationToken.None);

        Assert.False(notes.HasPending);
        Assert.Equal(GitHubReleaseNotes.Unavailable("3.4.0").Select(s => s.Version), notes.Sections.Select(s => s.Version));
    }
}
