# Flutter → iOS TestFlight via GitHub Actions (app-agnostic)

A reusable recipe for building a Flutter iOS app on a GitHub-hosted **macOS** runner
and uploading it to **TestFlight**, signed with **fastlane match** + an **App Store
Connect API key**. Copy the templates, replace the placeholders, add the secrets, push
a tag.

Placeholders used throughout:

| Placeholder | Meaning | Example |
|---|---|---|
| `com.yourco.yourapp` | App bundle id (must already exist in App Store Connect) | `com.acme.notes` |
| `YOURTEAMID` | Apple Developer **Team ID** (10 chars, App Store Connect → Membership) | `AB12CD34EF` |
| `YOURORG/ios-certificates` | An **empty private** git repo that match uses to store certs/profiles | |
| `ios/` | The Flutter project's iOS folder (relative to the repo working dir) | |

> Assumes a standard Flutter app whose iOS project is at `<flutter_project>/ios`. If your
> Flutter app is in a subfolder (e.g. `remote_app/`), set the workflow `working-directory`
> accordingly (shown below).

---

## 0. What you end up with

```
<flutter_project>/
├── ios/
│   ├── ExportOptions.plist          # manual signing for `flutter build ipa`
│   ├── Gemfile                      # fastlane
│   ├── Runner/Info.plist            # compliance + capability keys
│   └── fastlane/
│       ├── Appfile
│       ├── Matchfile
│       └── Fastfile                 # the `beta` lane
└── .github/workflows/ios-testflight.yml
```

Plus a separate **empty private repo** (`YOURORG/ios-certificates`) and a handful of
**GitHub Actions secrets**.

---

## 1. One-time Apple setup (do this before any CI run)

1. **App record must already exist.** Create the app in App Store Connect with the exact
   bundle id `com.yourco.yourapp`. Upload fails if the record doesn't exist — CI does not
   create it.
2. **App Store Connect API key** (App Store Connect → Users and Access → Integrations →
   App Store Connect API):
   - Create a key with role **App Manager** (Admin also works).
   - Note the **Key ID** and the **Issuer ID** (Issuer ID is shown once at the top of that page).
   - Download the `.p8` **once** (you cannot re-download it).
3. **Empty private certs repo.** Create `YOURORG/ios-certificates` — completely empty. match
   writes the encrypted distribution certificate + provisioning profiles here on first run.
4. **Team ID.** 10-char string from Membership details.

---

## 2. Files (templates)

### `ios/fastlane/Appfile`
```ruby
app_identifier("com.yourco.yourapp")
# Not needed when using an App Store Connect API key (see Fastfile).
# apple_id("you@example.com")
# team_id("YOURTEAMID")
```

### `ios/fastlane/Matchfile`
```ruby
git_url("https://github.com/YOURORG/ios-certificates.git")
storage_mode("git")
type("appstore")
# EVERY bundle id that ships in the build must be listed (app + each extension/watch target).
app_identifier(["com.yourco.yourapp"])
```

### `ios/ExportOptions.plist`
Used by `flutter build ipa`. **Manual** signing referencing the profiles match installs,
which are always named `match AppStore <bundle id>`.
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>method</key><string>app-store</string>
    <key>signingStyle</key><string>manual</string>
    <key>teamID</key><string>YOURTEAMID</string>
    <key>provisioningProfiles</key>
    <dict>
        <key>com.yourco.yourapp</key>
        <string>match AppStore com.yourco.yourapp</string>
        <!-- add a line here for every extra target (watch, notification service, …) -->
    </dict>
    <key>uploadSymbols</key><true/>
    <key>uploadBitcode</key><false/>
</dict>
</plist>
```

### `ios/Gemfile`
```ruby
source "https://rubygems.org"
gem "fastlane"
```

### `ios/fastlane/Fastfile`
```ruby
default_platform(:ios)

APP_IDS = ["com.yourco.yourapp"].freeze  # keep in sync with Matchfile + ExportOptions.plist

platform :ios do
  desc "Build the Flutter app and upload to TestFlight"
  lane :beta do
    api_key = app_store_connect_api_key(
      key_id: ENV.fetch("ASC_KEY_ID"),
      issuer_id: ENV.fetch("ASC_ISSUER_ID"),
      key_content: ENV.fetch("ASC_KEY_P8_BASE64"),
      is_key_content_base64: true
    )

    setup_ci                          # throwaway keychain for the signing identity
    ENV["MATCH_READONLY"] = "false"   # setup_ci forces this true; undo so first run can CREATE the cert

    match(
      type: "appstore",
      app_identifier: APP_IDS,
      readonly: false,                # first run creates the cert; flip to true once it exists
      api_key: api_key
    )

    # Absolute paths only: fastlane's CWD is fastlane/, but `flutter build ipa` runs
    # xcodebuild from inside ios/. Any relative path breaks. Anchor everything to fastlane dir.
    build_number = ENV["BUILD_NUMBER"] || "1"
    fastlane_dir = Dir.pwd                                                  # <project>/ios/fastlane
    export_opts  = File.expand_path("../ExportOptions.plist", fastlane_dir) # <project>/ios/ExportOptions.plist
    project_dir  = File.expand_path("../..", fastlane_dir)                  # <project> (has pubspec.yaml)

    # Flutter's default Runner target uses AUTOMATIC signing (no team) — the archive step
    # then can't find a cert on CI. Force MANUAL signing with the match cert + profile.
    update_code_signing_settings(
      path: File.expand_path("../Runner.xcodeproj", fastlane_dir),
      use_automatic_signing: false,
      team_id: "YOURTEAMID",
      code_sign_identity: "Apple Distribution",
      bundle_identifier: "com.yourco.yourapp",
      profile_name: "match AppStore com.yourco.yourapp",
      targets: ["Runner"]
    )

    sh("cd '#{project_dir}' && flutter build ipa --release " \
       "--build-number=#{build_number} " \
       "--export-options-plist='#{export_opts}'")

    ipa = Dir[File.join(project_dir, "build/ios/ipa/*.ipa")].first
    UI.user_error!("No IPA produced") if ipa.nil?

    upload_to_testflight(
      api_key: api_key,
      ipa: ipa,
      skip_waiting_for_build_processing: true
    )
  end
end
```

### `.github/workflows/ios-testflight.yml`
```yaml
name: iOS TestFlight

# Tag push (git tag v1.2.0 && git push --tags) or manual run.
on:
  push:
    tags: ['v*']
  workflow_dispatch:

jobs:
  build:
    runs-on: macos-15
    timeout-minutes: 45
    defaults:
      run:
        working-directory: .        # set to your Flutter subfolder, e.g. remote_app, if not repo root

    steps:
      - uses: actions/checkout@v4

      - name: Select latest stable Xcode
        uses: maxim-lobanov/setup-xcode@v1
        with:
          xcode-version: latest-stable

      - uses: subosito/flutter-action@v2
        with:
          channel: stable
          cache: true

      - name: Flutter deps
        run: flutter pub get

      - name: Ruby + Fastlane
        uses: ruby/setup-ruby@v1
        with:
          ruby-version: '3.3'
          bundler-cache: true
          working-directory: ios     # prefix with your Flutter subfolder if any: <subfolder>/ios

      - name: Build & upload to TestFlight
        working-directory: ios       # same prefix rule
        env:
          ASC_KEY_ID: ${{ secrets.ASC_KEY_ID }}
          ASC_ISSUER_ID: ${{ secrets.ASC_ISSUER_ID }}
          ASC_KEY_P8_BASE64: ${{ secrets.ASC_KEY_P8_BASE64 }}
          MATCH_PASSWORD: ${{ secrets.MATCH_PASSWORD }}
          MATCH_GIT_BASIC_AUTHORIZATION: ${{ secrets.MATCH_GIT_BASIC_AUTHORIZATION }}
          BUILD_NUMBER: ${{ github.run_number }}
        run: bundle exec fastlane beta
```

### `ios/Runner/Info.plist` keys (add as needed)
```xml
<!-- Auto-answer TestFlight's export-compliance question so builds don't sit in
     "Missing Compliance". Set to <true/> only if you add non-exempt crypto. -->
<key>ITSAppUsesNonExemptEncryption</key><false/>

<!-- Only if the app makes plain-HTTP calls (e.g. LAN / self-hosted server). -->
<key>NSAppTransportSecurity</key>
<dict>
  <key>NSAllowsArbitraryLoads</key><true/>
  <key>NSAllowsLocalNetworking</key><true/>
</dict>
<key>NSLocalNetworkUsageDescription</key>
<string>Describe why you talk to devices on the local network.</string>

<!-- Only if you play audio while backgrounded / screen locked. -->
<key>UIBackgroundModes</key>
<array><string>audio</string></array>
```

---

## 3. GitHub Actions secrets

Repo → Settings → Secrets and variables → Actions → **New repository secret**:

| Secret | What | How to get it |
|---|---|---|
| `ASC_KEY_ID` | App Store Connect API **Key ID** | ASC → Integrations |
| `ASC_ISSUER_ID` | ASC API **Issuer ID** | top of the Integrations page |
| `ASC_KEY_P8_BASE64` | the `.p8` file, **base64, single line** | `base64 -w0 AuthKey_XXXX.p8` (macOS: `base64 -i AuthKey_XXXX.p8`) |
| `MATCH_PASSWORD` | passphrase that encrypts the match repo | you choose it on first run; reuse forever |
| `MATCH_GIT_BASIC_AUTHORIZATION` | base64 of `username:PAT` for the **certs repo** | `printf 'USER:GHP_TOKEN' \| base64` — PAT needs `repo` scope |

---

## 4. First-run bootstrap (creating the cert)

The **first** successful run has to *create* the distribution certificate and store it in
the certs repo. That's why the Fastfile sets `readonly: false` and clears `MATCH_READONLY`.

1. Ensure `YOURORG/ios-certificates` exists and is **empty**.
2. Push a tag (or run the workflow manually). match creates the cert + a
   `match AppStore com.yourco.yourapp` profile, commits them to the certs repo, and the
   build proceeds.
3. **After** the first green run you may flip `readonly: true` in the Fastfile (and drop the
   `MATCH_READONLY` override) so CI never mutates signing assets again. Optional but tidy.

> An Apple account allows a limited number of distribution certificates. Letting match
> manage one shared cert (via the repo) across all your machines/CI avoids hitting the cap.

---

## 5. Triggering a build

```bash
git tag v1.2.0
git push origin v1.2.0
```
or Actions tab → *iOS TestFlight* → **Run workflow**.

**Version name** comes from your `pubspec.yaml` `version:` (the `x.y.z` part →
`CFBundleShortVersionString`). **Build number** comes from `github.run_number` (passed as
`BUILD_NUMBER`). TestFlight requires the build number to be **unique and increasing for a
given version string** — `run_number` is monotonic, so this is handled automatically.

---

## 6. Gotchas discovered the hard way

Each of these is a real failure mode, not theory.

1. **Runner target defaults to *Automatic* signing.** A fresh Flutter iOS project has the
   Runner target on automatic signing with no team. On CI (no logged-in Xcode account) the
   archive step can't find a cert and fails. **Fix:** `update_code_signing_settings(...,
   use_automatic_signing: false, ...)` in the Fastfile *before* `flutter build ipa`, pointing
   at the `match AppStore <bundle id>` profile. (Editing the `.xcodeproj` by hand also works
   but drifts; do it in the lane so it's reproducible.)

2. **Relative paths break because two tools have different CWDs.** fastlane runs from
   `ios/fastlane/`, but `flutter build ipa` invokes `xcodebuild` from `ios/`. A relative
   `ExportOptions.plist` or output path resolves against the wrong dir and fails. **Fix:**
   compute **absolute** paths anchored to `Dir.pwd` (the fastlane dir) with `File.expand_path`,
   as in the template.

3. **`setup_ci` silently forces `MATCH_READONLY=true`.** That blocks the very first run from
   *creating* the certificate. **Fix:** set `ENV["MATCH_READONLY"] = "false"` right after
   `setup_ci`, and `readonly: false` on `match`, at least until the cert exists.

4. **"Missing Compliance" parks the build in TestFlight.** Without an encryption declaration,
   every upload waits for a manual answer before testers can install. **Fix:**
   `ITSAppUsesNonExemptEncryption` in Info.plist (`false` for standard/exempt crypto like HTTPS).

5. **iOS blocks plain HTTP (ATS).** If the app talks to `http://` (LAN box, self-hosted API),
   requests silently fail on device. **Fix:** `NSAppTransportSecurity` with
   `NSAllowsArbitraryLoads`/`NSAllowsLocalNetworking`, plus `NSLocalNetworkUsageDescription`
   (iOS 14+ shows a local-network permission prompt).

6. **Background audio stops on lock without the capability.** Audio playback is killed when
   the app backgrounds unless you declare it. **Fix:** `UIBackgroundModes = [audio]`.
   (Lock-screen transport controls need an audio-session/now-playing integration on top —
   separate concern.)

7. **Base64 secrets: no stray newline.** `base64` wraps at 76 cols by default; a multi-line
   secret + `is_key_content_base64: true` can corrupt the key, and GitHub trims a trailing
   newline inconsistently. **Fix:** encode single-line: `base64 -w0` (GNU) / `base64 -i` (macOS).

8. **The App Store Connect app record must pre-exist.** `upload_to_testflight` won't create
   it; you get a confusing "app not found". Create the app + bundle id in ASC first.

9. **Duplicate build number = rejected upload.** Re-running against the same
   `(version, build number)` is refused. Drive the build number off `github.run_number` (or
   otherwise bump it every run).

10. **Every shipping bundle id must be everywhere.** Watch app, notification-service, share
    extension — each id must appear in **Matchfile**, **ExportOptions.plist**
    `provisioningProfiles`, the **Fastfile** `APP_IDS`, and its Xcode target. Miss one and
    signing/export fails. Profiles are always named `match AppStore <bundle id>`.

11. **`skip_waiting_for_build_processing: true`.** Otherwise the lane blocks for Apple's
    processing (minutes) and can time the job out for no benefit.

12. **Podfile is generated, not committed by default.** `flutter build ipa` runs `pod install`
    and generates `ios/Podfile` if missing — fine on CI. But native plugins (audio, etc.) pull
    CocoaPods; if a plugin needs a higher iOS deployment target, bump `platform :ios` in the
    Podfile (commit it) or the pod install fails.

13. **`latest-stable` Xcode can jump versions.** A new Xcode can change signing/SDK behavior
    overnight. If a green pipeline suddenly breaks with no code change, pin
    `xcode-version:` to a specific version instead of `latest-stable`.

14. **API key role.** The ASC API key needs **App Manager** (or Admin). A key with only
    Developer/limited access uploads-fails late in the run.

15. **`working-directory` for subfolder projects.** If the Flutter app isn't at repo root, set
    the workflow `working-directory` (and the `ios` steps' path) to `<subfolder>/ios`. The
    Fastfile's relative `File.expand_path` math still holds because it's anchored to `Dir.pwd`.

16. **`ITMS-90683: Missing purpose string` for APIs you don't call.** Apple's static analysis
    scans **linked SDKs/plugins**, not just your code. Audio plugins (`just_audio`,
    `audio_session`, etc.) link `AVAudioSession`, which references the microphone API — so the
    upload is rejected for a missing `NSMicrophoneUsageDescription` even though you never record.
    This arrives as an **email after upload** (the build fails processing), not at build time.
    **Fix:** add the key with an honest string; the app doesn't have to actually use the API:
    ```xml
    <key>NSMicrophoneUsageDescription</key>
    <string>This app does not record audio; the permission exists only because the audio playback engine links the microphone API.</string>
    ```
    Other common ones surfaced the same way: `NSBluetoothAlwaysUsageDescription` (BLE audio
    routing), `NSAppleMusicUsageDescription` (media library). Add whichever the email names.

---

## 7. Quick troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `No signing certificate "Apple Distribution" found` | Automatic signing / readonly match on first run | gotcha 1 + 3 |
| `Could not find ExportOptions.plist` / archive path errors | relative paths | gotcha 2 |
| Build stuck "Missing Compliance", testers can't install | no encryption key | gotcha 4 |
| Network calls fail only on device | ATS blocks HTTP | gotcha 5 |
| Audio stops when screen locks | no background mode | gotcha 6 |
| `Invalid API key` / auth errors | multi-line/base64 p8, wrong role | gotcha 7 + 14 |
| `app not found` on upload | ASC app record missing | gotcha 8 |
| `redundant binary upload` / build number exists | duplicate build number | gotcha 9 |
| Signing works locally, fails on CI | bundle id not in all 4 places | gotcha 10 |
| Job times out after archive | waiting on Apple processing | gotcha 11 |
