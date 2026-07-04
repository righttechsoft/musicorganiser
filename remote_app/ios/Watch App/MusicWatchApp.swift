import SwiftUI

// Native watchOS remote for Music Organiser. Talks straight to the desktop control API
// over the LAN (http://<ip>:8787) — no phone/Flutter dependency. The phone's Flutter app
// is untouched; this is a separate Xcode target sharing only the App Store Connect app group.
@main
struct MusicWatchApp: App {
    @StateObject private var client = WatchClient()

    var body: some Scene {
        WindowGroup {
            ContentView().environmentObject(client)
        }
    }
}
