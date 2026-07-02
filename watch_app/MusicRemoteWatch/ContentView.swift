import SwiftUI

struct ContentView: View {
    @StateObject private var client = PlayerClient()

    private var isPlaying: Bool { client.status?.isPlaying ?? false }

    private var titleText: String {
        if client.host.isEmpty { return "Set host in ⚙︎" }
        if let t = client.status?.nowPlaying?.title, !t.isEmpty { return t }
        return client.reachable ? "Nothing playing" : "Offline"
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 10) {
                // Now playing
                VStack(spacing: 2) {
                    Text(titleText)
                        .font(.headline)
                        .lineLimit(2)
                        .multilineTextAlignment(.center)
                    if let a = client.status?.nowPlaying?.artist, !a.isEmpty {
                        Text(a)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }

                // Transport
                HStack(spacing: 12) {
                    Button { client.previous() } label: {
                        Image(systemName: "backward.fill")
                    }
                    Button { client.playPause() } label: {
                        Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                            .font(.title2)
                            .frame(maxWidth: .infinity)
                    }
                    .tint(.accentColor)
                    Button { client.next() } label: {
                        Image(systemName: "forward.fill")
                    }
                }
                .buttonStyle(.bordered)

                // Volume — drag the slider or use the Digital Crown. Posts once movement
                // settles. Changes that merely echo the polled value are ignored.
                HStack(spacing: 8) {
                    Image(systemName: "speaker.fill").font(.caption2).foregroundStyle(.secondary)
                    Slider(value: $client.volume, in: 0...100, step: 1) { editing in
                        client.adjustingVolume = editing
                    }
                    Image(systemName: "speaker.wave.3.fill").font(.caption2).foregroundStyle(.secondary)
                }
                .focusable(true)
                .digitalCrownRotation(
                    $client.volume, from: 0, through: 100, by: 1,
                    sensitivity: .low, isContinuous: false, isHapticFeedbackEnabled: true
                )
                .onChange(of: client.volume) { _, newValue in
                    // Skip the poll echo: when refresh() writes the desktop's volume back in.
                    if Int(newValue.rounded()) == client.status?.volume { return }
                    client.adjustingVolume = true
                    volumeSettleTask?.cancel()
                    volumeSettleTask = Task {
                        try? await Task.sleep(nanoseconds: 400_000_000)
                        if Task.isCancelled { return }
                        client.adjustingVolume = false
                        client.setVolume(Int(newValue.rounded()))
                    }
                }
            }
            .padding(.horizontal, 4)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    NavigationLink { SettingsView(client: client) } label: {
                        Image(systemName: "gear")
                    }
                }
            }
            .onAppear { client.start() }
            .onDisappear { client.stop() }
        }
    }

    @State private var volumeSettleTask: Task<Void, Never>?
}

#Preview {
    ContentView()
}
