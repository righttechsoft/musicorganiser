# iOS → TestFlight via GitHub Actions (no Mac required)

GitHub's macOS runner builds the Flutter **iOS app** and uploads it to TestFlight. Signing
cert + provisioning profiles are created **on the runner** by `fastlane match` — you never
need your own Mac for the phone app.

Files: `.github/workflows/ios-testflight.yml`, `ios/Gemfile`,
`ios/fastlane/{Appfile,Matchfile,Fastfile}`, `ios/ExportOptions.plist`.

> **Apple Watch app:** shipping it needs the watch target added in Xcode (a real Mac,
> once). Everything below ships the **phone app only**. Add the watch later — see the last
> section.

## Prereqs (all doable from Windows + a browser) — status

- ✅ Team ID `CRRN9GCAL9` — already in `ExportOptions.plist` / used by match.
- ✅ App ID `com.righttechsoft.musicOrganiserRemote` registered.
- ✅ App Store Connect app record created.
- ⚠️ **API key must be _Admin_ role.** `match` *creates the distribution certificate* on the
  first CI run, and the App Store Connect API only allows that with an **Admin** key.
  App Manager is NOT enough for cert creation. If your key is App Manager: Users and Access →
  Integrations → App Store Connect API → **generate a new key with role _Admin_**, download
  the `.p8`, and update the `ASC_KEY_ID` + `ASC_KEY_P8_BASE64` secrets. (App Manager would
  work only if the cert already existed.)
- ⬜ **Certs repo:** create an *empty private* GitHub repo, e.g. `YOURORG/ios-certificates`.
  Put its URL in `ios/fastlane/Matchfile`. `match` stores the encrypted cert/profiles there.

## GitHub secrets (repo → Settings → Secrets and variables → Actions)

| Secret | Value |
|---|---|
| `ASC_KEY_ID` | API key ID (e.g. `RX8P8NZPU2`) — must be an **Admin** key |
| `ASC_ISSUER_ID` | Issuer ID (above the keys list in App Store Connect) |
| `ASC_KEY_P8_BASE64` | the `.p8` as base64 — `base64 -w0 AuthKey_XXXX.p8` |
| `MATCH_PASSWORD` | a passphrase **you invent** — match encrypts the certs repo with it. Save it. |
| `MATCH_GIT_BASIC_AUTHORIZATION` | base64 of `user:PAT` for the certs repo. **PAT needs _write_** (match pushes the new cert on first run). `echo -n "USER:ghp_xxx" \| base64` |

Do **not** set `MATCH_READONLY` yet — leaving it unset lets the first run create the cert.

## Run it

1. Push a tag: `git tag v1.0.0 && git push origin v1.0.0` (or Actions → iOS TestFlight →
   Run workflow).
2. First run: `match` creates the distribution cert + an app-store profile and commits them
   (encrypted) to the certs repo; then `flutter build ipa` signs with them; then it uploads
   to TestFlight. Build number = the GitHub run number.
3. After ~5–15 min of Apple processing, the build shows in **TestFlight** (in the app
   record). Add yourself as an **Internal Tester** → install the TestFlight app on your
   iPhone → the build appears. No screenshots or app review needed for internal testing.
4. *(optional, after the first green run)* add a repo secret `MATCH_READONLY` = `true` so
   later runs only read the certs instead of trying to regenerate.

## If a first run fails on cert creation
Either the key isn't Admin, or your account already has the max distribution certs. Fallback
without a Mac: generate a CSR with openssl, create the Distribution cert in the developer
portal, then `fastlane match import` it into the certs repo. Ask me and I'll give the exact
commands.

## Adding the Apple Watch app later (needs Xcode once)
On a Mac (or a cloud Mac — MacinCloud, GitHub Codespaces won't do Xcode GUI):
1. Open `remote_app/ios/Runner.xcworkspace`, add a **watchOS App** target, bundle id
   `com.righttechsoft.musicOrganiserRemote.watchkitapp`, drop in the files from
   `watch_app/MusicRemoteWatch/`, add the ATS keys, set the watch target's versions to
   `$(FLUTTER_BUILD_NAME)` / `$(FLUTTER_BUILD_NUMBER)`. Commit `ios/`.
2. Register that bundle id (already done) and re-add it to `Matchfile`, `Fastfile` (`APP_IDS`)
   and `ExportOptions.plist`. Next tagged run builds both and TestFlight installs the watch
   app alongside the phone.
