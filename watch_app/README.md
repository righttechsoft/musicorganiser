# Music Organiser — Apple Watch remote

Basic watchOS transport for the desktop control API (`/status`, `/playback/*`, `/volume`):
now-playing title, **prev / play-pause / next**, and a **volume** slider (Digital Crown too).
It talks to the desktop directly over the LAN — same host:port as the phone app.

## ⚠️ Must be built on a Mac with Xcode
watchOS apps are native Swift/SwiftUI; they cannot be built on Windows or from Flutter.
These `.swift` files are the whole app — you add them to a watch target in Xcode. This
source has **not** been compiled or run (no Mac here); expect to fix small things in Xcode.

## Files
- `MusicRemoteWatch/MusicRemoteWatchApp.swift` — app entry (`@main`).
- `MusicRemoteWatch/ContentView.swift` — transport + volume UI.
- `MusicRemoteWatch/SettingsView.swift` — host/port entry + connection status.
- `MusicRemoteWatch/PlayerClient.swift` — networking + 2s status polling.
- `MusicRemoteWatch/PhoneLink.swift` — WatchConnectivity receiver (auto host:port from phone).
- `MusicRemoteWatch/Models.swift` — `PlayerStatus` decode.
- `Info-ATS-snippet.plist` — cleartext-HTTP allowance to paste into the target Info.plist.

## Add it in Xcode

You can ship it alongside the Flutter iOS app, or as its own project.

**Alongside the Flutter app (recommended):**
1. Open `remote_app/ios/Runner.xcworkspace` in Xcode.
2. **File → New → Target… → watchOS → App**. Name it e.g. `MusicRemoteWatch`,
   interface **SwiftUI**, language **Swift**. Let it create the scheme.
3. Xcode generates a `…App.swift` and a `ContentView.swift` in the new target.
   **Delete the generated `ContentView.swift` and the generated `…App.swift`**, then drag
   the five files from `MusicRemoteWatch/` into the watch target (check *Copy items* and
   the watch target under *Add to targets*).
   - If you keep Xcode's generated `@main` file instead, delete
     `MusicRemoteWatchApp.swift` from this folder — only one `@main` may exist.
4. Watch target → **Info** tab → add the keys from `Info-ATS-snippet.plist`
   (open as source, paste inside the top `<dict>`).
5. Select the watch scheme, pick a paired Watch simulator (or your watch), **Run**.

**Standalone:** File → New → Project → watchOS → App, then do steps 3–5.

## Auto host:port from the phone (WatchConnectivity)

The phone pushes the host:port to the watch automatically — no manual entry needed —
**but only when the watch app is a companion target of the Flutter iOS app** (added into
`Runner.xcworkspace` as above, so iOS pairs the two). A standalone watch project won't
receive it; use manual entry there.

Already wired for you:
- **iOS Runner** (`remote_app/ios/Runner/AppDelegate.swift`): activates `WCSession` and
  exposes a `music_organiser/watch` method channel.
- **Dart** (`lib/watch_link.dart`, called from `AppState._saveRecent`): every time the
  phone connects, it sends `{host, port}` to the watch via `updateApplicationContext`.
- **Watch** (`PhoneLink.swift` → `PlayerClient`): receives it, saves it, reconnects.

So: connect the phone app once → the watch picks up the same desktop automatically (even
if the watch app was closed; the context is delivered on next launch).

## Use
1. **Auto:** connect the phone app to the desktop → the watch configures itself.
2. **Manual fallback:** on the watch, tap **⚙︎ gear** → enter **Host** (desktop LAN IP,
   e.g. `192.168.1.20`) and **Port** (`8787`) → **Save**. Persisted.
3. Transport + volume control the desktop. Status refreshes every 2s.

## Notes / limits
- The watch must reach the desktop's LAN IP — same Wi-Fi, or routed via the paired
  iPhone when it's nearby. `localhost`/`10.0.2.2` are emulator-only; use the real IP.
- WatchConnectivity auto-config needs the watch app installed as the iOS app's companion
  (paired watch, both apps installed). Manual entry always works as a fallback.
- Same "no auth, LAN only" model as the rest of the system.
