// Downstream-only: upstream has no self-update and no release notes in the app (#347).

using System.Net.Http.Headers;
using System.Text.Json;

namespace Daqifi.Desktop.UpdateVersion;

/// <summary>A release as the GitHub releases API lists it.</summary>
/// <param name="Version">The release's version, from its tag without the leading "v", for example <c>3.4.0-beta.1</c>.</param>
/// <param name="IsPrerelease">GitHub's "pre-release" flag.</param>
/// <param name="Body">The release body in Markdown, or <c>null</c>.</param>
/// <param name="Page">The release's page on GitHub.</param>
public sealed record PublishedRelease(string Version, bool IsPrerelease, string? Body, string Page);

/// <summary>The notes of one release, under its version.</summary>
public sealed record ReleaseNotesSection(string Version, IReadOnlyList<NoteBlock> Blocks);

/// <summary>One page of a releases API answer, and the address of the next page, or <c>null</c> on the last.</summary>
public sealed record ReleasesPage(string Json, Uri? Next);

/// <summary>
/// The notes for an offered version. <see cref="HasPending"/> is true only when the answer held the
/// offered version's own release: anything else (an empty or unreadable list, or one without that
/// release) is worth asking again at the next check.
/// </summary>
public sealed record ReleaseNotesResult(IReadOnlyList<ReleaseNotesSection> Sections, bool HasPending);

/// <summary>
/// The release notes of the pending update, from this repository's GitHub releases (#347). The
/// release body is the one source of the notes: the app keeps no copy of them.
/// </summary>
/// <remarks>
/// A user can be several versions behind, so the notes are those of every release after the
/// installed version up to the pending one, newest first (<see cref="Between"/>). Prereleases are in
/// that range only for a copy that follows prereleases, the same rule the update service uses to
/// choose its channel: a version with a prerelease label follows prereleases.
/// <para>
/// The releases API takes no token here, so GitHub allows few requests from one address. The
/// UPDATES section fetches only when an update is on offer, once for each version, and reads pages
/// of <see cref="PAGE_SIZE"/> releases only until one lists the installed release
/// (<see cref="LoadAsync"/>), at most <see cref="MaxPages"/>.
/// </para>
/// </remarks>
public static class GitHubReleaseNotes
{
    private const string REPOSITORY = "daqifi/daqifi-app";
    private const int PAGE_SIZE = 100;

    /// <summary>The first page of the releases API, newest release first.</summary>
    public static readonly Uri FirstPage = new($"https://api.github.com/repos/{REPOSITORY}/releases?per_page={PAGE_SIZE}");

    /// <summary>The page that lists every release, for notes older than the pages read.</summary>
    public const string AllReleasesPage = $"https://github.com/{REPOSITORY}/releases";

    /// <summary>The most pages of <see cref="PAGE_SIZE"/> releases one load reads.</summary>
    public const int MaxPages = 10;

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub refuses API requests without a User-Agent.
        if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd($"DAQiFi/{Common.AppVersion.Semantic?.Replace('+', '.')}"))
        {
            client.DefaultRequestHeaders.UserAgent.TryParseAdd("DAQiFi");
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    /// <summary>
    /// Reads one page of the releases API from GitHub, without a token, and the address of the next
    /// page from its <c>Link</c> header. The one place the notes touch the network;
    /// <see cref="ViewModels.AppUpdateViewModel"/> takes it as a delegate, so tests give their own pages.
    /// </summary>
    public static async Task<ReleasesPage> FetchPageAsync(Uri page, CancellationToken cancellationToken)
    {
        using var response = await Client.Value.GetAsync(page, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ReleasesPage(json, NextPage(response.Headers.TryGetValues("Link", out var links) ? string.Join(",", links) : null));
    }

    /// <summary>
    /// The <c>rel="next"</c> address in a <c>Link</c> header, or <c>null</c>. Only an https address on
    /// the API's own host is followed.
    /// </summary>
    public static Uri? NextPage(string? linkHeader)
    {
        if (string.IsNullOrWhiteSpace(linkHeader)) { return null; }

        foreach (var link in linkHeader.Split(','))
        {
            var parts = link.Split(';');
            var address = parts[0].Trim();
            var isNext = parts.Skip(1).Any(p => p.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
            if (!isNext || address.Length < 2 || address[0] != '<' || address[^1] != '>') { continue; }

            return Uri.TryCreate(address[1..^1], UriKind.Absolute, out var next)
                   && next.Scheme == Uri.UriSchemeHttps
                   && next.Host == FirstPage.Host
                ? next
                : null;
        }

        return null;
    }

    /// <summary>
    /// Reads the releases from <see cref="FirstPage"/> on, and gives the notes for <paramref name="pending"/>.
    /// The API lists releases by when each release was made, newest first, prereleases included, so
    /// the range can go on past the first page: the next page is read until a page lists the
    /// installed release itself (<see cref="ReachesInstalled"/>), the history ends, or
    /// <see cref="MaxPages"/> pages are read. The releases read are then filtered and sorted by
    /// version (<see cref="Between"/>). A user a few versions behind finds the installed release on
    /// the first page, so one request is the usual case. When the cap stops the reading first, the
    /// notes end with a section that says so (#695 review).
    /// </summary>
    /// <exception cref="Exception">Whatever <paramref name="fetchPage"/> throws, for example with no network.</exception>
    public static async Task<ReleaseNotesResult> LoadAsync(
        Func<Uri, CancellationToken, Task<ReleasesPage>> fetchPage, string? installed, string pending, CancellationToken cancellationToken)
    {
        var releases = new List<PublishedRelease>();
        var address = FirstPage;
        for (var read = 1; ; read++)
        {
            var page = await fetchPage(address, cancellationToken);
            if (Read(page.Json) is not { } onPage) { return new(Unavailable(pending), false); }

            releases.AddRange(onPage);
            if (page.Next is null || ReachesInstalled(onPage, installed, pending))
            {
                return Notes(releases, installed, pending, truncated: false);
            }

            if (read >= MaxPages) { return Notes(releases, installed, pending, truncated: true); }
            address = page.Next;
        }
    }

    /// <summary>
    /// True when <paramref name="page"/> lists the installed version's own release, so no later page
    /// holds a release in the range. Without a version for this copy, only the pending release is in
    /// the range, so the page that lists it is enough.
    /// </summary>
    /// <remarks>
    /// Not "a version at or below the installed one" (#695 review, round 2). The API orders releases
    /// by when each was made (its <c>created_at</c>: measured on this repository, the time the
    /// release was created, minutes after its tag's commit), not by version. A backport such as
    /// 3.2.9, made after 3.6.0, is listed before 3.5.0 and 3.4.0, so an older version number says
    /// nothing about what is still to come. The installed release does: the release workflow makes
    /// each release when its tag is built, so every release made after the installed one is listed
    /// before it. When that release is not in the history (deleted, or never published), nothing
    /// marks the end of the range, and the reading goes to the end of the history or the cap.
    /// </remarks>
    private static bool ReachesInstalled(IEnumerable<PublishedRelease> page, string? installed, string pending) =>
        ReleaseVersion.TryParse(installed, out _)
            ? page.Any(r => SameVersion(r.Version, installed!))
            : page.Any(r => SameVersion(r.Version, pending));

    /// <summary>The page of the release of <paramref name="version"/>.</summary>
    public static string ReleasePage(string version) =>
        $"https://github.com/{REPOSITORY}/releases/tag/v{Uri.EscapeDataString(TrimVersion(version))}";

    /// <summary>The releases in a releases API answer. Drafts, and tags that are no version, are left out.</summary>
    /// <exception cref="JsonException">The answer is not JSON.</exception>
    /// <exception cref="InvalidOperationException">The answer is JSON of another shape.</exception>
    public static IReadOnlyList<PublishedRelease> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var releases = new List<PublishedRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) { continue; }
            if (!release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) { continue; }

            var version = TrimVersion(tag.GetString()!);
            if (!ReleaseVersion.TryParse(version, out _)) { continue; }

            releases.Add(new PublishedRelease(
                version,
                release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True,
                release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
                release.TryGetProperty("html_url", out var page) && page.ValueKind == JsonValueKind.String
                    && NotesMarkdown.IsWebLink(page.GetString())
                    ? page.GetString()!
                    : ReleasePage(version)));
        }

        return releases;
    }

    /// <summary>
    /// The releases after <paramref name="installed"/> up to and including <paramref name="pending"/>,
    /// newest first. Prereleases between the two are left out unless <paramref name="installed"/> is
    /// one; the pending release is in, as the update service has already offered it. Without a
    /// version for this copy, only the pending release is certain to be new, so only it is returned.
    /// </summary>
    public static IReadOnlyList<PublishedRelease> Between(IEnumerable<PublishedRelease> releases, string? installed, string pending)
    {
        if (!ReleaseVersion.TryParse(pending, out var to)) { return []; }

        var hasFrom = ReleaseVersion.TryParse(installed, out var from);
        var followsPrereleases = hasFrom && from.IsPrerelease;

        return releases
            .Select(release => (Release: release, Parsed: ReleaseVersion.TryParse(release.Version, out var v) ? v : (ReleaseVersion?)null))
            .Where(r => r.Parsed is { } v
                        && v.CompareTo(to) <= 0
                        && (hasFrom ? v.CompareTo(from) > 0 : v.CompareTo(to) == 0)
                        && (followsPrereleases || !r.Release.IsPrerelease || v.CompareTo(to) == 0))
            .OrderByDescending(r => r.Parsed!.Value)
            .Select(r => r.Release)
            .ToList();
    }

    /// <summary>
    /// The notes the UPDATES section shows for <paramref name="pending"/> from one page: each release
    /// in the range under its version, newest first. A release with no body, a pending version the
    /// answer does not list, and an answer that cannot be read each give <see cref="Unavailable"/>.
    /// Never throws.
    /// </summary>
    public static IReadOnlyList<ReleaseNotesSection> Sections(string json, string? installed, string pending) =>
        Read(json) is { } releases ? Notes(releases, installed, pending, truncated: false).Sections : Unavailable(pending);

    /// <summary>
    /// The notes for <paramref name="pending"/> from the releases read: each release in the range
    /// under its version, newest first. Without the pending release they are <see cref="Unavailable"/>.
    /// </summary>
    /// <param name="truncated">
    /// True when <see cref="MaxPages"/> stopped the reading before the installed version. The notes
    /// then end with a section that says older notes are not shown, and links every release.
    /// </param>
    public static ReleaseNotesResult Notes(IReadOnlyList<PublishedRelease> releases, string? installed, string pending, bool truncated)
    {
        var inRange = Between(releases, installed, pending);
        if (!inRange.Any(r => SameVersion(r.Version, pending))) { return new(Unavailable(pending), false); }

        var sections = inRange
            .Select(release => NotesMarkdown.Parse(release.Body) is { Count: > 0 } blocks
                ? new ReleaseNotesSection(release.Version, blocks)
                : Unavailable(release.Version, release.Page))
            .ToList();
        if (truncated) { sections.Add(EarlierReleases); }
        return new(sections, true);
    }

    private static readonly ReleaseNotesSection EarlierReleases = new("Earlier releases",
    [
        new NoteParagraph(
        [
            new NoteSpan($"Only the newest {(MaxPages * PAGE_SIZE).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} " +
                         "releases were read, so the notes of older releases are not shown. "),
            new NoteSpan("See all releases", Link: AllReleasesPage),
        ]),
    ]);

    /// <summary>The releases of one page, or <c>null</c> when it cannot be read.</summary>
    private static List<PublishedRelease>? Read(string json)
    {
        try
        {
            return [.. Parse(json)];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool SameVersion(string a, string b) =>
        ReleaseVersion.TryParse(a, out var x) && ReleaseVersion.TryParse(b, out var y) && x.CompareTo(y) == 0;

    /// <summary>What is shown when the notes of <paramref name="version"/> cannot be shown: a line, and a link to its release page.</summary>
    public static IReadOnlyList<ReleaseNotesSection> Unavailable(string version) =>
        [Unavailable(TrimVersion(version), ReleasePage(version))];

    private static ReleaseNotesSection Unavailable(string version, string page) =>
        new(version, [new NoteParagraph([new NoteSpan("Release notes unavailable. "), new NoteSpan("See the release page", Link: page)])]);

    /// <summary>A tag or version without its leading "v" and any build metadata.</summary>
    private static string TrimVersion(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) { trimmed = trimmed[1..]; }
        var metadata = trimmed.IndexOf('+');
        return metadata < 0 ? trimmed : trimmed[..metadata];
    }

    /// <summary>
    /// A semantic version, compared the way the updater orders releases: by major, minor and patch,
    /// then a release after its prereleases, and prerelease labels part by part, numbers as numbers
    /// (beta.10 comes after beta.9).
    /// </summary>
    private readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ReleaseVersion>
    {
        public bool IsPrerelease => Prerelease.Length > 0;

        public static bool TryParse(string? text, out ReleaseVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) { return false; }

            var trimmed = TrimVersion(text);
            var dash = trimmed.IndexOf('-');
            var core = (dash < 0 ? trimmed : trimmed[..dash]).Split('.');
            var label = dash < 0 ? string.Empty : trimmed[(dash + 1)..];
            if (core.Length != 3 || (dash >= 0 && label.Length == 0)) { return false; }
            if (!int.TryParse(core[0], System.Globalization.NumberStyles.None, null, out var major)
                || !int.TryParse(core[1], System.Globalization.NumberStyles.None, null, out var minor)
                || !int.TryParse(core[2], System.Globalization.NumberStyles.None, null, out var patch))
            {
                return false;
            }

            version = new ReleaseVersion(major, minor, patch, label);
            return true;
        }

        public int CompareTo(ReleaseVersion other)
        {
            var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
            if (core != 0) { return core; }
            if (IsPrerelease != other.IsPrerelease) { return IsPrerelease ? -1 : 1; }

            var mine = Prerelease.Split('.');
            var theirs = other.Prerelease.Split('.');
            for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
            {
                var part = ComparePart(mine[i], theirs[i]);
                if (part != 0) { return part; }
            }

            return mine.Length.CompareTo(theirs.Length);
        }

        private static int ComparePart(string a, string b)
        {
            var aNumber = long.TryParse(a, System.Globalization.NumberStyles.None, null, out var x);
            var bNumber = long.TryParse(b, System.Globalization.NumberStyles.None, null, out var y);
            if (aNumber && bNumber) { return x.CompareTo(y); }
            if (aNumber != bNumber) { return aNumber ? -1 : 1; }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
