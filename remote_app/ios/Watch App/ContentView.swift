import SwiftUI

struct ContentView: View {
    @EnvironmentObject var client: WatchClient

    var body: some View {
        Group {
            if client.connected {
                PlayerView()
            } else {
                ConnectView()
            }
        }
        .onAppear { if client.connected { client.start() } }
    }
}

// First-run: type the desktop's LAN IP. One-time; persisted in UserDefaults.
// ponytail: manual IP entry. Upgrade path = WatchConnectivity handoff of host:port from the phone app.
struct ConnectView: View {
    @EnvironmentObject var client: WatchClient
    @State private var ip = ""

    var body: some View {
        ScrollView {
            VStack(spacing: 8) {
                Text("Desktop IP").font(.headline)
                TextField("192.168.1.20", text: $ip)
                    .textContentType(.URL)
                Text("Port 8787").font(.caption2).foregroundStyle(.secondary)
                Button("Connect") { client.setIP(ip) }
                    .disabled(ip.isEmpty)
            }
            .padding(.horizontal, 4)
        }
    }
}

struct PlayerView: View {
    @EnvironmentObject var client: WatchClient
    @State private var crown = 50.0

    var body: some View {
        VStack(spacing: 6) {
            if !client.reachable {
                HStack(spacing: 6) {
                    Label("Offline", systemImage: "wifi.slash")
                        .font(.caption2).foregroundStyle(.orange)
                    Button("Change IP") { client.forget() }
                        .font(.caption2).buttonStyle(.plain).foregroundStyle(.blue)
                }
            }

            let np = client.status.nowPlaying
            Text(np?.name ?? "Nothing playing")
                .font(.headline).lineLimit(2).multilineTextAlignment(.center)
            Text(np?.artist ?? "")
                .font(.caption2).foregroundStyle(.secondary).lineLimit(1)

            HStack(spacing: 12) {
                ctrl("backward.fill") { client.playback("previous") }
                ctrl(client.status.isPlaying ? "pause.fill" : "play.fill") {
                    client.playback("playpause")
                }.font(.title2)
                ctrl("forward.fill") { client.playback("next") }
            }
            .padding(.vertical, 2)

            HStack(spacing: 18) {
                ctrl(client.status.shuffle ? "shuffle.circle.fill" : "shuffle") {
                    client.playback("shuffle")
                }
                ctrl(repeatIcon) { client.playback("repeat") }
            }
            .font(.body).foregroundStyle(.secondary)

            HStack {
                Image(systemName: "speaker.fill").font(.caption2)
                ProgressView(value: Double(client.volume), total: 100)
                Image(systemName: "speaker.wave.3.fill").font(.caption2)
            }
        }
        .focusable()
        .digitalCrownRotation($crown, from: 0, through: 100, by: 2,
                              sensitivity: .medium, isContinuous: false)
        .onChange(of: crown) { _, v in
            let level = Int(v.rounded())
            if level != client.volume { client.setVolume(level) }
        }
        .onChange(of: client.volume) { _, v in
            if Int(crown.rounded()) != v { crown = Double(v) }
        }
        .onAppear { crown = Double(client.volume) }
    }

    private var repeatIcon: String {
        switch client.status.repeatMode {
        case "one": return "repeat.1"
        case "all": return "repeat.circle.fill"
        default: return "repeat"
        }
    }

    private func ctrl(_ icon: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) { Image(systemName: icon) }
            .buttonStyle(.plain)
    }
}
