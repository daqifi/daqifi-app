# Distributing DAQiFi

How this app reaches users on five platforms, which channels are worth the work, and which are
closed to us for reasons that will not change by trying harder.

Written 2026-10-29 against `main` at `ff11582`. Every claim about our own tree was checked
against the tree; every claim about a store's rules carries its source. Where a fact could not be
settled from a primary source it is in [§9](#9-open-questions--verify-before-committing) as a
question, not asserted here as a fact.

This is the channel strategy. It is **not** a restatement of #341, which decides how the desktop
app *updates itself* once installed. The two are tightly coupled — [§3](#3-one-updater-per-channel-the-rule-that-keeps-channels-from-fighting)
is the coupling — but "which stores do we appear in" and "how does an installed copy get the next
version" are different questions and #341 only answers the second.

---

## 1. Where we actually are

Not aspirationally. This is what exists in the tree and in the world today.

| Head | Packaging that exists | Signed? | Published anywhere? |
|---|---|---|---|
| **Windows** (`win-x64`) | None. `dotnet publish` output, no installer. | No | One hand-built 54 MB zip, `v3.3.0-beta.1`, **0 downloads** |
| **macOS** (`osx-arm64`, `osx-x64`) | `DAQiFi.app` bundle via `Daqifi.Avalonia.Desktop/macOS/MacAppBundle.targets` — complete Info.plist, icon, `NSLocalNetworkUsageDescription` | **No** — deliberately not even ad-hoc (#350) | No |
| **Linux** (`linux-x64`) | None. CI builds the head on `ubuntu-latest` every PR and publishes nothing. | n/a | No |
| **Android** | Release `aab`, API 36, signing wired through `env:` indirection, `tools/play/build-release-aab.sh`, `tools/play/publish.py` | **Yes** — `daqifi-upload`, SHA-256 recorded in the csproj | **Play internal track, versionCode 4** |
| **iOS** | Simulator builds only. CI gates `iossimulator-arm64`. | No — no Apple account (#109) | No |

Two things to take from this table.

**Android is almost done and everything else is at zero.** Android has a keystore, a dedicated
publishing GCP project with a correctly minimised blast radius, a dry-run-first upload script, a
versionCode-to-commit ledger, finished listing copy and five store graphics. That is the full
apparatus of a shipping channel. The other four heads have between a bundle layout and nothing.

**There is no release pipeline at all.** `.github/workflows/` holds a compile gate and three
policy guards. #344 already says this. It bears repeating here because *every* channel below
consumes a release artifact, so #344 is not one item among many — it is the floor under all of
them.

For reference, the thing being replaced: `daqifi-desktop` ships exactly one channel, a WiX MSI
built by `release.yaml` on a version tag and attached to a **draft** GitHub Release. Unsigned.
Windows only. No store, no package manager, no update mechanism. We are not porting a
distribution strategy; there isn't one to port.

---

## 2. The two constraints that decide every channel

Channel selection for this app is not a marketing question. Two properties of the product rule
out most of the candidate list before preference enters, and both are easy to discover late and
expensive to discover late.

### 2.1 The desktop app is not sandboxable as written

The desktop head needs, from the OS:

| Capability | Where | Used for |
|---|---|---|
| `System.IO.Ports` — raw serial | `ConnectionManager.cs`, `SerialPortPollingDeviceWatcher.cs`, `FirmwareUpdateCoordinator.cs:706,818` | USB device connection, port enumeration, firmware flash at 9600 baud |
| `System.Management` (WMI) | `Daqifi.Avalonia.csproj:106` | Windows device enumeration |
| **Windows Firewall rule creation** | `Configuration/FirewallManager.cs` | Letting discovery traffic through; **requires administrator** |
| Subnet-directed **UDP broadcast**, port 30303 | `Services/NativeWiFiDiscovery.cs` | Device discovery — not mDNS, so there is no service type to declare |
| `Process.Start` of a native flash tool | firmware update path | PIC32 and WINC1500 flashing |
| Elevation-dependent data directory | `App.cs:48`, `AppDataPaths` | Machine-wide when elevated, per-user otherwise |

A store sandbox is a bet that an app needs none of that. This one needs all of it. That single
fact is why the recommendation below puts **direct download first on all three desktop platforms**
and treats every desktop store as a secondary channel with its own feasibility question.

Note what this does *not* affect: mobile. Both mobile heads are Wi-Fi/TCP only by recorded
divergence **DIV-UI-003** — `System.IO.Ports` and `System.Management` restore their netstandard
fallbacks there and throw if touched. Android reaches USB through `UsbManager` and a runtime
permission grant (`Daqifi.Avalonia.Android/Usb/`), which is the sanctioned Android API, not a
sandbox escape. **The serial problem is a desktop-only problem**, and the mobile store channels
are untouched by it.

### 2.2 No store reviewer can test this app

Every listing below is for an application that does nothing without a ~$500 piece of hardware on
the same network. A reviewer opens it, sees an empty device list, and has to decide what that
means.

**Apple states the requirement outright, and it is stricter than a screen recording.** Apple's
complete-review guidance is explicit, in one bullet under "Provide information about the app":

> **Required hardware:** Apps with required hardware must provide a video (not a screen
> recording) that shows your app running on a physical Apple device as it pairs and interacts
> with the hardware.

— [Complete your app's review information](https://developer.apple.com/help/app-review/before-submitting-for-review/complete-review/)

So for the iOS head (and for the Mac App Store, if it is ever pursued) the deliverable is a
**camera-recorded video of a physical Apple device beside a Nyquist**, showing connection and
interaction. A capture of the app's own screen — however real the data behind it — is the
artifact Apple's text names and excludes. Preparing one would invite exactly the 2.1
"Information Needed" rejection this section exists to prevent. Budget a camera, a tripod and
bench time, not a screen-recording utility.

Separately, 2.1 is the most common first-pass rejection, and a built-in demo mode substituting
for a demo account needs Apple's **prior** approval and must expose full functionality
([2.1 in practice](https://rejectproof.com/blog/guideline-2-1-information-needed-demo-account)).

**For the non-Apple stores, a screen recording plus written notes is the proportionate answer**,
and there the Nyquist's own test-pattern generator does the work. `store/listing-copy.md`
documents it —

```
SYSTem:STReam:TEST:PATtern 6     # 256-sample sine, per-channel phase offset
```

— and all four Play screenshots were shot with it. Neither Play nor the Microsoft Store imposes
Apple's physical-device rule, so there the pattern makes a perfectly good demonstration of real
packets over a real link. Note that the generator is still worth using *in* the Apple video: it
gives the traces something legible to show. It just does not substitute for pointing a camera at
the device.

**Budget a hardware demonstration and written review notes as a required deliverable of every
store submission**, not as a reaction to a rejection — a camera video for Apple, a screen
recording elsewhere. `store/listing-copy.md`'s "REQUIRES DAQiFi HARDWARE" paragraph, placed
deliberately near the top of the Play description, is the same defence aimed at users instead of
reviewers; keep it in every listing.

One recorded data point, from the Avalonia app that shipped to the Mac App Store in August 2026
(the thread in §5.2): App Review raised nothing about sandboxing or entitlements. The one
rejection was a 2.1 — because the submitter pasted their review notes in truncated. The notes are
the part that fails.

---

## 3. One updater per channel: the rule that keeps channels from fighting

Worth stating once, because getting it wrong ships an app that fights itself.

A channel either owns updates or it does not, and the shipped binary has to agree with the
channel it came from:

- **Direct download** — Velopack owns updates (#341, #343).
- **Microsoft Store via MSIX** — the Store owns updates.
- **Microsoft Store via MSI/EXE** — *we* own updates. Microsoft's own comparison is explicit that
  the Store does not push updates to installers and the app is responsible for its own
  ([distribution paths](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path)).
- **Mac App Store, Play, App Store** — the store owns updates, and self-updating binaries are
  forbidden. #341 already scopes mobile self-update out for exactly this reason.
- **Homebrew cask / WinGet** — the package manager *tracks* releases but a cask declaring
  `auto_updates true` is skipped by `brew upgrade` unless greedy, which is the correct
  declaration for a Velopack app ([Cask Cookbook](https://docs.brew.sh/Cask-Cookbook)).
- **Flathub / Snap** — the store owns updates and the sandbox will not let the app replace
  itself anyway.

So `IAppUpdateService` needs a notion of "this build's updates are owned elsewhere" that is baked
in **at package time**, not detected at runtime. #341 already requires the service to report
"unsupported" and the UI to hide itself on mobile; the same switch serves every store channel.
Build it as a build-time property on the Desktop head from the start — retrofitting it once a
Store build exists means a Store user being offered a Velopack update that cannot possibly apply.

This is the one architectural consequence of this document, and it is cheap only if it lands with
#343 rather than after it.

---

## 4. The recommendation

| Platform | Channel | Verdict | Gate |
|---|---|---|---|
| **Windows** | Direct download (Velopack `Setup.exe`) | **Ship first** | #343, #344 |
| | WinGet | **Ship second** — cheapest reach we have | A signed, silently-installable `Setup.exe` |
| | Microsoft Store (MSI/EXE) | **Worth it** | Authenticode cert (#342) |
| | Microsoft Store (MSIX) | **Investigate** — free signing, manifest firewall rules | A feasibility spike |
| **macOS** | Direct download (Velopack `.pkg`), signed + notarized | **Ship first** | #109, #350 |
| | Own Homebrew tap | **Ship second** — trivial once notarized | A notarized release |
| | `homebrew/cask` proper | **Blocked, not by us** | Notability: needs 225 stars / 90 forks / 90 watchers |
| | Mac App Store | **Open question, not now** | Test single-file CoreCLR before assuming #102 |
| **Linux** | AppImage | **Ship first** | #351 |
| | Flathub | **Worth it, later** | An offline NuGet build |
| | Snap Store | **Skip** | `serial-port` is a restricted interface |
| | `.deb` / `.rpm` | **Skip** | Decided in #351; Flathub is the better answer |
| **Android** | Google Play | **Already live, internal track** | Production rollout gated on #119 |
| | F-Droid | **Possible, low value** | Opt-in Sentry; build-from-source |
| | Amazon / Samsung / Huawei | **Skip** | No audience overlap |
| **iOS** | App Store (+ TestFlight) | **Only channel that exists** | #109, #104, #102 |

---

## 5. Per platform

### 5.1 Windows

**Direct download is the primary channel and stays the primary channel.** Velopack's
`Setup.exe` installs per-user to `%LocalAppData%\<packId>` with no UAC prompt, which #341 already
notes matches what `AppDataPaths` does for un-elevated runs. `vpk pack` also emits a portable
`.zip` and can emit an MSI (`--buildMsi`, Windows-only build host) for the enterprise customers
who will ask for one ([packaging overview](https://docs.velopack.io/packaging/overview)).

**WinGet is the highest-leverage second channel and almost free.** A manifest in
`microsoft/winget-pkgs` pointing at the release asset, nothing hosted by us, no review queue in
any meaningful sense. One hard requirement: the community repo **requires silent and
silent-with-progress install**, and packages that cannot install silently are refused
([manifest docs](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest)).
Confirm Velopack `Setup.exe`'s silent switch and declare it in `InstallerSwitches` — flagged in
[§9](#9-open-questions--verify-before-committing) because I could not confirm the switch from
Velopack's docs.

**Microsoft Store, the EXE/MSI route.** The Store has accepted unpackaged installers since June
2021 and the submission points at an installer we host. The catch that matters for sequencing:
**Microsoft does not re-sign it.** The installer and its PE files must chain to a CA in the
Microsoft Trusted Root Program, and self-signed is refused
([MSI/EXE package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements)).
So #342 is not merely a SmartScreen nicety — **it is a hard gate on the Store channel.** It also
requires an offline installer that does not change after submission, which rules out a stub
downloader.

**Microsoft Store, the MSIX route, deserves a spike rather than a dismissal.** Two findings argue
for it against my initial expectation:

1. **Signing is free and the warnings disappear.** Microsoft re-signs MSIX packages submitted to
   the Store; the user never sees SmartScreen and we never buy or renew a certificate
   ([code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)).
2. **MSIX would *fix* our firewall problem, not break it.** Firewall rules are a declarable
   package extension: Windows creates them on install, rewrites them for the new path on update,
   and removes them on uninstall ([Advanced Installer](https://www.advancedinstaller.com/firewall-rules-msix.html),
   [a real-world PR doing it](https://github.com/CrossPaste/crosspaste-desktop/pull/5288)). That
   is strictly better than `FirewallManager.cs`'s current behaviour, which needs administrator
   and otherwise shows a "configure firewall manually" prompt.

A packaged desktop app with `runFullTrust` runs at medium integrity, **not** in an AppContainer,
so the capability model that would forbid serial access does not apply the way it does to UWP
([capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations),
[how packaged apps run](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)).
What a spike has to answer is whether `System.IO.Ports`, WMI enumeration and `Process.Start` of
the flash tool survive MSIX's filesystem and registry virtualisation. The honest state of this is
"promising and unverified" — and the cost of being wrong is one afternoon, because an MSIX package
either launches and finds a device or it does not.

**Code signing — three corrections to #342.** This matters because #342 is the long-lead item
whose options were priced before the ground moved:

- **EV no longer buys instant SmartScreen trust.** #342's table says EV "buys immediate
  SmartScreen reputation rather than earning it over downloads". **That behaviour was removed in
  2024.** EV-signed files now build reputation exactly as OV does, and Microsoft's own guidance
  is that paying the EV premium for SmartScreen is "no longer justified"
  ([code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)).
  The one argument for EV in #342 is therefore void.
- **Trusted Signing is now Azure Artifact Signing**, ~$9.99/mo for 5,000 signatures, available to
  organisations in the USA/Canada/EU/UK. Microsoft's current page states the geography but **does
  not state the three-year-history requirement** #342 cites; that appears only in secondary
  sources. DAQiFi should clear three years either way, but the thing to check first is that it
  does not run on a free or trial Azure subscription. Velopack supports it natively via
  `--azureTrustedSignFile`, and that path works from a Linux or macOS runner too
  ([Velopack signing](https://docs.velopack.io/packaging/signing)).
- **New option #342 does not list: [SignPath Foundation](https://signpath.io) gives free OV-level
  code signing to qualifying open-source projects.** `daqifi-app` is public and MIT. This may make
  the Windows certificate free rather than $120/yr, and it is worth an application before paying
  for anything — Microsoft's own page recommends it for open source.

### 5.2 macOS

**Direct download, signed and notarized, is the primary channel.** #350 has the reasoning right
and does not need relitigating: on macOS unsigned is not a warning, it is a refusal, and an
updater that replaces a working install with a bundle Gatekeeper won't launch is worse than no
updater. Velopack does this end to end — `--signAppIdentity`, `--signInstallIdentity`,
`--notaryProfile`, and it drives `codesign` and `xcrun notarytool` to **automatically sign and
notarize** both app and installer packages ([Velopack signing](https://docs.velopack.io/packaging/signing)).
So the work here is credentials, not code.

One detail from Velopack's docs with a direct bearing on the Mac App Store question below:
**Velopack does not support the App Sandbox entitlement.** The direct-download path and the Mac
App Store path are not two configurations of one pipeline; they are two pipelines.

**Homebrew: our own tap, not `homebrew/cask`.** I checked the numbers. `homebrew/cask` requires,
for a **self-submission by the repo owner**, "at least 90 forks, 90 watchers or 225 stars", and a
repo under 30 days old is normally ineligible
([Package Acceptance Policy](https://docs.brew.sh/Package-Acceptance-Policy)). `daqifi/daqifi-app`
today has **2 stars, 0 forks, 0 watchers**. We are not close, and no amount of engineering
changes it.

A tap we own (`daqifi/homebrew-tap`) has no notability bar at all, is one small repo, and gives
users `brew install --cask daqifi/tap/daqifi`. Declare `auto_updates true` so `brew upgrade`
defers to Velopack, and add a `livecheck` block against GitHub Releases. Casks must also pass
Homebrew's Gatekeeper checks without the user bypassing them, which the notarized build satisfies
automatically ([Acceptable Casks](https://docs.brew.sh/Acceptable-Casks)). Revisit `homebrew/cask`
if the repo ever crosses the threshold; until then this is the same user experience at a fraction
of the cost.

**Mac App Store: a sandboxed CoreCLR build does not start, and there are two candidate
workarounds — only one of which has been ruled in or out.** Here is the evidence rather than a
conclusion, because the conclusion is genuinely open.

A .NET app with **App Sandbox** enabled aborts at startup on current macOS:

```
Sandbox: MyApp(61975) deny(1) mach-lookup com.apple.coreservices.launchservicesd
HIServices: _RegisterApplication(), unable to get application ASN from launchservicesd,
            and this application requires an ASN, so aborting.
```

A team working on exactly our stack — .NET 10.0.11, Avalonia 12.1.0, Apple Silicon — reduced this
to a ~15-line console app that only `dlopen`s AppKit and calls `[NSApplication sharedApplication]`,
with no Avalonia at all. They ruled out entitlements (including every `cs.*` combination), bundle
contents, launch path, timing, JIT, code-signing state and deployment target, each by measurement.
Replacing only the main binary of the same signed bundle with a Swift program registers fine. The
conclusion: something CoreCLR does *while running*
([AvaloniaUI/Avalonia#6529](https://github.com/AvaloniaUI/Avalonia/issues/6529)).

**NativeAOT does not hit it.** A minimal Avalonia 12.1.0 app published with `PublishAot=true`
opens its window under App Sandbox with only `app-sandbox`, `application-identifier` and
`team-identifier` — no `cs.*` entitlements at all. **And it shipped: live on the Mac App Store on
2026-08-19**, signed with `3rd Party Mac Developer Application` + `Mac Installer Distribution`,
packaged as a `.pkg`.

**But NativeAOT is one workaround, not the only route — and the cheaper one was never compared
against.** The list above is thorough about what it ruled out, and **single-file publishing is
not on it**. That matters, because single-file is the route Avalonia's own documentation
prescribes for this exact symptom:

> If your app crashes when running inside the sandbox, try publishing with the single file option
> enabled.

— [Avalonia macOS deployment guide](https://docs.avaloniaui.net/docs/deployment/macos), whose
example command is `--self-contained true -p:PublishSingleFile=true`.

The same thread carries it too. An Avalonia maintainer's position is that for a store app
"single file is not a solution, it's a requirement", and the other developer in the thread —
whose sandboxed app "works fine in direct distribution and in app store mode (if the entitlement
is included)" — measured single-file startup at within 200 ms of the alternative and concluded
they would "build a single-file application for apple app store". That is a CoreCLR route to MAS,
and nobody in the thread reports it failing.

**So the dependency on #102 is not established.** NativeAOT would land MAS on the wall
`Daqifi.Avalonia.iOS.csproj` documents at length — three AOT-only failures in the data layer, the
third being that **every** query needs EF Core's experimental `precompile-queries`, which fails
on this codebase because its queries capture instance fields ("Encountered unknown identifier
name `_session`"). That is a real wall, and it is why *the NativeAOT route* is expensive. It is
not a reason to call the channel closed, because the documented single-file route may not need
AOT at all.

**The serial problem from §2.1 is a second, independent question** and does not resolve with
either route: a sandboxed MAS build would need `com.apple.security.device.serial` (a real
entitlement, but with no Xcode checkbox and added by hand
([Apple's key reference](https://developer.apple.com/library/archive/documentation/Miscellaneous/Reference/EntitlementKeyReference/Chapters/EnablingAppSandbox.html)))
and would still have no route to `Process.Start` of the firmware flash tool.

**Recommendation: do not pursue the Mac App Store now, but record it as an open feasibility
question rather than as gated on #102.** If it is ever taken up, the order is: **test the
documented single-file CoreCLR packaging first** — it is a publish flag and an afternoon — and
only fall back to NativeAOT, and therefore to #102, if that fails. Writing it down as
"NativeAOT-gated" would retire a channel on the strength of one report that never tested the
cheaper option.

Note the three practical traps the thread records for the NativeAOT path (Homebrew dylibs
breaking hardened runtime over Team IDs, native libs needing to be inside the bundle and signed
by us, and a provisioning-profile mismatch presenting as `Launchd job spawn failed`) so nobody
rediscovers them.

### 5.3 Linux

**AppImage first, exactly as #351 argues.** That ticket's reasoning is sound and current: AppImage
is the one Linux format that self-updates cleanly, it is the only one Velopack builds, and VS Code
declining Linux self-update in favour of the package manager is the right precedent. We publish
nothing for Linux today, so this adds a platform rather than changing one.

**Flathub is the right second Linux channel — and it is more work than it looks.** Two
requirements shape it:

- **The build has no network access.** Flathub states plainly that there is no network access
  during the build and `--share=network` will not work
  ([Flathub requirements](https://docs.flathub.org/docs/for-app-authors/requirements)). A .NET app
  therefore cannot `dotnet restore` during the build; every NuGet package must be declared as a
  pinned source in the manifest. **This is where `packages.lock.json` pays off**: the repo already
  records the full transitive closure with content hashes for every project, which is exactly the
  input a generated Flatpak sources manifest needs. The policy in `Directory.Build.props` was
  written for supply-chain reasons and happens to make this channel tractable.
- **Serial access needs `--device=all`.** Flatpak has no narrower exemption for serial device
  nodes, because they are dynamic and the sandbox's `/dev` is set up once
  ([flatpak#3959](https://github.com/flatpak/flatpak/issues/3959)). Flathub asks that static
  permissions be "kept to an absolute minimum", so this needs justifying in review — but it is an
  accepted pattern for hardware apps; the Android Studio Flathub manifest uses it.

Also relevant: Flathub owns updates, so the Flatpak build needs §3's switch. And Flathub rejects
"any software requiring daily updates" — not a constraint we come near.

**Skip the Snap Store.** Snap's `serial-port` is a **restricted interface** precisely because it
grants privileged access to serial hardware. It does not auto-connect; auto-connection needs a
store-side assertion requested in the snapcraft forum, and the documented convention for serial is
to declare the slot in a *gadget* snap, which is a brand-store concept with no bearing on a
general desktop app ([serial-port interface](https://snapcraft.io/docs/reference/interfaces/serial-port-interface/),
[auto-connection](https://snapcraft.io/docs/explanation/interfaces/interface-auto-connection/)).
Absent an approved assertion, every user must run `snap connect` by hand before the app can see a
device — which, for an app whose entire job is talking to a device, is a broken first run. Classic
confinement is the escape hatch and reviewers require that strict confinement be tried and its
denials shown first. The cost is a review negotiation and the prize is a channel Flathub already
covers.

**No `.deb`/`.rpm`**, per #351. Users who want those have their package manager; today they have
nothing from us at all, so there is no regression.

### 5.4 Android

**Play is the channel, it is nearly done, and the remaining work is #119's, not this document's.**
versionCode 4 sits on the internal track. What stands between that and production is listed in
#119 — the main store listing still renders as "com.daqifi.app (unreviewed)" with a generic icon
despite all five assets sitting in `store/`, and crash reporting has never been *confirmed to
arrive* (#114). Neither is a distribution-strategy question.

Two compliance clocks to put on the calendar, since nothing in the repo tracks them:

- **Target API.** We are already compliant and deliberately so — `net10.0-android36.0` is pinned
  in the TFM with a comment explaining that the bare moniker is an implicit default that moves.
  Play requires API 36 for new apps and updates from **2026-08-31** (extensions were available to
  2026-11-01), while existing apps need API 35 to stay available to new users
  ([Play Console Help](https://support.google.com/googleplay/android-developer/answer/11926878)).
  The pinning decision already paid for itself.
- **16 KB page size.** Targeting API 35+ already requires 16 KB support on 64-bit devices, and
  Google's guidance says updates without it cannot be released from **2027-02-01**. Framework
  support is not the risk; native `.so` payloads in NuGet packages are, and we ship SkiaSharp and
  HarfBuzz ([.NET MAUI's write-up](https://devblogs.microsoft.com/dotnet/maui-google-play-16-kb-page-size-support/),
  [Platform.Uno's](https://platform.uno/blog/what-android-16-kb-page-size-requirement-means-for-net-developers/)).
  This is checkable today against the existing `aab` and should be, before the deadline turns it
  into an emergency.

**F-Droid is possible and probably not worth it.** The licence is right (MIT) and Sentry is FLOSS,
so it is not disqualified. Two frictions: F-Droid requires any crash reporting to be **explicitly
opt-in** — asked on first run or a preference defaulting to OFF — or the app is flagged with the
`Tracking` anti-feature and hidden from the default client view
([F-Droid FAQ](https://f-droid.org/en/docs/FAQ_-_App_Developers/), [Anti-Features](https://f-droid.org/en/docs/Anti-Features/)).
Our Sentry is on by default, though `-p:SentryDsn=` already compiles it out, so a FOSS flavour is
mechanically easy. And F-Droid builds from source, which for a .NET Android app is unproven
territory. The deciding question is whether any DAQiFi user is an F-Droid user; my guess is
approximately none, and the opt-in rework is the kind of change that touches the Data safety
declarations the Play listing is already consistent with. **Revisit only on user demand.**

**Skip Amazon Appstore, Samsung Galaxy Store and Huawei AppGallery.** Each is a separate listing,
separate review and separate compliance surface, for an audience that overlaps our buyers at
roughly zero. The people who own a Nyquist have a Play device or an iPhone.

### 5.5 iOS

**The App Store is the only channel, so the question is only whether we can get in.** Three gates,
all already filed:

- **#109 — Apple Developer account.** Blocks everything. Organisation enrolment needs D-U-N-S
  verification, which is days to weeks of somebody else's queue. This is the single longest-lead
  item in this entire document.
- **#104 — the multicast entitlement.** Discovery is a UDP broadcast sweep, and
  `com.apple.developer.networking.multicast` is a **separate Apple approval** with its own review,
  on top of enrolment. #109 already makes the important point: if it is refused, the honest story
  is the manual connect-by-IP path, and that is a product decision rather than a bug. Apply the
  day the account exists.
- **#102 — EF Core under the interpreter.** The head runs today with `MtouchInterpreter` naming
  five EF assemblies. That is a shipping configuration, not a blocker — but note the csproj's own
  warning that CI covers only the simulator, where the trimmer is off, while **device builds are
  `SdkOnly`-trimmed either way**. The first device build is where trimming problems surface, and
  nothing before #109 can run one.

**The required-hardware video is an iOS deliverable with a physical dependency.** §2.2 has the
rule and Apple's wording; the consequence for scheduling is that it needs a provisioned iPhone, a
Nyquist, and a second camera in the same room at the same time. It cannot be produced from the
simulator, and it cannot be produced before #109. Plan it alongside the first device build rather
than at submission.

**Use TestFlight as the real beta channel for every platform's benefit.** It is the one store
channel with a usable pre-release track, and the 2.1 review-notes discipline from §2.2 can be
rehearsed there at no cost.

---

## 6. The identity critical path

The engineering above is weeks of work. The identity below is weeks of *waiting*, and it is the
thing that actually sets the schedule. Both #341 and #119 already say this about their own
dependencies; collected, it is the plan's real spine.

| Item | Lead time | Blocks | State |
|---|---|---|---|
| **Apple Developer account (#109)** | Days–weeks (D-U-N-S) | All iOS, all macOS signing, both Apple stores | **Not started** |
| Apple multicast entitlement (#104) | Separate review, after enrolment | iOS discovery | Blocked by #109 |
| **Windows Authenticode (#342)** | Days–weeks | Microsoft Store (**hard gate**), SmartScreen | **Not started** |
| Play Console listing + App content | Maintainer's queue | Production rollout | Assets ready in `store/` |
| Privacy policy URL | Ours to host | Every store listing | README has the text; **no hosted URL** |

Three notes:

1. **#109 and #342 should be started in the same sitting, today.** Neither depends on any code in
   this repo. Both are pure waiting. Every week they are not started is a week added to the end.
2. **Apply to SignPath Foundation before buying a Windows certificate.** If it comes through, #342
   costs nothing; if it does not, Azure Artifact Signing is next and Velopack already speaks it.
3. **The privacy policy needs a URL, not a README section.** Play already required one for the
   live listing, so one may exist — if it does it should be linked from this repo, because Apple
   and Microsoft will both ask, and `store/listing-copy.md` warns that Play checks the listing text
   against the Data safety answers and the published policy for consistency.

---

## 7. Sequencing

Ordered by what unblocks the most, not by platform.

**Now, in parallel, no code involved**
1. Start Apple Developer enrolment (#109).
2. Apply to SignPath Foundation; if refused, start Azure Artifact Signing (#342).
3. Confirm or publish the privacy policy URL.
4. Finish the Play main store listing and flip to production (#119).

**Foundation — everything else waits on these**
5. `IAppUpdateService` + Velopack in the Desktop head (#343), **including §3's build-time
   update-ownership switch**.
6. `release.yml` (#344): all four desktop RIDs, tag-vs-`DaqifiAppVersion` guard, `app.manifest`
   guard, locked-mode restore, signing hooks inert until credentials exist.

**First real channels**
7. Windows direct download. Signed if #342 landed; shipping unsigned is already the recorded
   decision in #341 if it has not.
8. Linux AppImage (#351) — no external dependency at all, which makes it the cheapest second
   platform.
9. macOS direct download, signed + notarized (#350), the moment #109 clears.

**Second channels, each small once the first exists**
10. WinGet manifest — confirm the silent switch first.
11. Own Homebrew tap.
12. Microsoft Store via MSI/EXE, once #342 has landed.

**Spikes, time-boxed, run when they stop being free**
13. MSIX feasibility: does serial + WMI + the flash tool survive it, and do manifest firewall
    rules replace `FirewallManager.cs`? One afternoon; the prize is free signing and a better
    firewall story.
14. Flathub: generate the offline NuGet sources manifest from `packages.lock.json` and see whether
    a Flatpak build completes.

**Gated on other work, not scheduled here**
15. iOS App Store — after #109 and #104.
16. Mac App Store — not scheduled. If it is ever taken up, the first step is a single-file
    CoreCLR publish under App Sandbox (a publish flag, an afternoon), **not** #102. Only if that
    fails does the channel depend on the NativeAOT/EF Core work.

**Measure 16 KB page-size compliance on the current `aab` before 2027-02-01.** It is a ten-minute
check and a potential multi-week surprise.

---

## 8. Corrections to tickets already filed

Things in open issues that this research contradicts. Worth fixing in place so they do not get
re-reasoned from.

- **#342's EV row is out of date.** "EV buys immediate SmartScreen reputation" stopped being true
  in 2024; EV now builds reputation like OV, and Microsoft advises against paying the premium for
  SmartScreen. #342's only stated reason to consider EV is therefore gone.
- **#342 does not mention SignPath Foundation**, which may make the Windows certificate free for an
  MIT-licensed public repo. It should be the first thing tried.
- **#342 frames the certificate as "deliberately not a blocker".** True for SmartScreen, false for
  the Microsoft Store: the MSI/EXE route requires a Trusted-Root-chaining certificate and refuses
  self-signed. If the Store is a channel we want, #342 is a hard gate on it.
- **#341's platform matrix treats the Mac App Store as absent rather than as an open question.**
  A sandboxed CoreCLR build does not start, but there are two candidate workarounds and only the
  expensive one (NativeAOT, which implies #102) has been tested. The documented single-file route
  has not. Worth recording as "open — test single-file first" so the channel is neither assumed
  shut nor assumed to cost an EF Core refactor.
- **The Trusted Signing three-year-history requirement in #342 is not on Microsoft's current
  page.** It may still be true, but it is secondary-source only; the documented limits are
  geographic, plus no free/trial Azure subscription.

---

## 9. Open questions — verify before committing

Each of these changes a decision above and none could be settled from a primary source.

1. **Velopack `Setup.exe` silent-install switch.** WinGet's community repo *requires* silent and
   silent-with-progress. The switch is not in the docs I could reach. If `Setup.exe` cannot install
   silently, WinGet needs the MSI (`--buildMsi`) instead — which changes §5.1's "almost free".
2. **Is an Azure Artifact Signing certificate accepted for the Microsoft Store MSI/EXE route?**
   Microsoft's own comparison table marks Azure Artifact Signing "Store eligible: No", which reads
   oddly given its certificates chain to a Trusted Root Program CA. If that "No" is literal, one
   certificate does **not** serve both direct download and the Store, and #342's option analysis
   needs redoing.
3. **Does `System.IO.Ports` work inside an MSIX package?** Decides §5.1's MSIX spike outright.
   Equally: does `Process.Start` of the firmware flash tool survive MSIX virtualisation?
4. **Does `com.apple.security.device.serial` actually cover `System.IO.Ports`' POSIX `open()` of
   `/dev/cu.*`?** The entitlement is documented for serial devices and reports of it working are
   second-hand. Only matters if the Mac App Store is ever revisited.
5. **Does a single-file CoreCLR publish start under App Sandbox?** Avalonia's own deployment guide
   prescribes `--self-contained true -p:PublishSingleFile=true` for exactly the sandbox startup
   crash in §5.2, and the NativeAOT report that found the crash never compared against it. This is
   the question that decides whether the Mac App Store is NativeAOT-gated (and therefore #102's
   problem) or a publish flag away. **It is also the cheapest question on this list** — one
   publish, one launch — and it should be answered before anyone writes the channel off.
6. **Is .NET 10's Android runtime 16 KB page-size compliant, including SkiaSharp and HarfBuzz?**
   Framework support is documented for MAUI 9+; our native NuGet payloads are the risk. Checkable
   against the existing `aab` today.
7. **Does a hosted DAQiFi privacy policy URL already exist** for the Play listing? If so it should
   be in this repo; if not, it is a prerequisite for three more stores.
8. **Flathub and `--device=all`:** accepted with justification, or pushed back on? Precedent says
   accepted for hardware apps, but it is a review conversation and worth asking in the submission
   PR rather than discovering late.

---

## Sources

Our own tree: `Directory.Build.props`, `Daqifi.Avalonia.Desktop/macOS/MacAppBundle.targets`,
`Daqifi.Avalonia.Desktop/macOS/Info.plist`, `Daqifi.Avalonia.{Android,iOS}.csproj`,
`tools/play/README.md`, `store/listing-copy.md`, `.github/workflows/build.yml`,
`Daqifi.Avalonia/Daqifi.Desktop/Configuration/FirewallManager.cs`, and issues #98, #102, #104,
#109, #119, #341–#353. `daqifi-desktop`: `.github/workflows/release.yaml`,
`Daqifi.Desktop.Setup/`.

External:
[Windows distribution paths](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path) ·
[MSI/EXE package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements) ·
[Windows code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) ·
[App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations) ·
[How packaged desktop apps run](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes) ·
[MSIX firewall rules](https://www.advancedinstaller.com/firewall-rules-msix.html) ·
[CrossPaste MSIX firewall PR](https://github.com/CrossPaste/crosspaste-desktop/pull/5288) ·
[WinGet manifest docs](https://learn.microsoft.com/en-us/windows/package-manager/package/manifest) ·
[SignPath Foundation](https://signpath.io) ·
[Velopack packaging](https://docs.velopack.io/packaging/overview) ·
[Velopack signing](https://docs.velopack.io/packaging/signing) ·
[Velopack distributing](https://docs.velopack.io/distributing/overview) ·
[Homebrew Package Acceptance Policy](https://docs.brew.sh/Package-Acceptance-Policy) ·
[Acceptable Casks](https://docs.brew.sh/Acceptable-Casks) ·
[Cask Cookbook](https://docs.brew.sh/Cask-Cookbook) ·
[Apple App Sandbox entitlement reference](https://developer.apple.com/library/archive/documentation/Miscellaneous/Reference/EntitlementKeyReference/Chapters/EnablingAppSandbox.html) ·
[AvaloniaUI/Avalonia#6529 — Mac App Store sandbox crash, the NativeAOT report and the single-file route](https://github.com/AvaloniaUI/Avalonia/issues/6529) ·
[Avalonia macOS deployment — sandbox startup crashes and PublishSingleFile](https://docs.avaloniaui.net/docs/deployment/macos) ·
[Apple — complete your app's review information (the required-hardware video rule)](https://developer.apple.com/help/app-review/before-submitting-for-review/complete-review/) ·
[Apple 2.1 review notes in practice](https://rejectproof.com/blog/guideline-2-1-information-needed-demo-account) ·
[Flathub requirements](https://docs.flathub.org/docs/for-app-authors/requirements) ·
[flatpak#3959 — serial device access](https://github.com/flatpak/flatpak/issues/3959) ·
[flatpak#3016 — narrower device exemptions](https://github.com/flatpak/flatpak/issues/3016) ·
[Snap serial-port interface](https://snapcraft.io/docs/reference/interfaces/serial-port-interface/) ·
[Snap interface auto-connection](https://snapcraft.io/docs/explanation/interfaces/interface-auto-connection/) ·
[Reviewing classic confinement snaps](https://snapcraft.io/docs/reference/administration/reviewing-classic-confinement-snaps/) ·
[F-Droid Inclusion Policy](https://f-droid.org/docs/Inclusion_Policy/) ·
[F-Droid FAQ for app developers](https://f-droid.org/en/docs/FAQ_-_App_Developers/) ·
[F-Droid Anti-Features](https://f-droid.org/en/docs/Anti-Features/) ·
[Play target API requirements](https://support.google.com/googleplay/android-developer/answer/11926878) ·
[Play 16 KB page size](https://developer.android.com/guide/practices/page-sizes) ·
[.NET MAUI and 16 KB pages](https://devblogs.microsoft.com/dotnet/maui-google-play-16-kb-page-size-support/) ·
[16 KB pages for .NET developers](https://platform.uno/blog/what-android-16-kb-page-size-requirement-means-for-net-developers/)
