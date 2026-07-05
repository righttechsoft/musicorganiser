import SwiftUI

/// Thin URLSession client + 1.5s status poll. Mirrors the Flutter ApiClient's control subset.
/// Desktop API: GET /status, POST /playback/{playpause,next,previous,shuffle,repeat}, POST /volume.
@MainActor
final class WatchClient: ObservableObject {
    // Persisted desktop LAN address. Port is fixed at 8787 (matches ControlApiService).
    // ponytail: port hard-coded; add a port field only if the desktop port becomes configurable.
    // @Published (not @AppStorage — that only publishes from a View) so the UI switches
    // connect<->player when it changes; UserDefaults is the persistence.
    @Published var ip: String = UserDefaults.standard.string(forKey: "desktopIP") ?? ""

    @Published var status: PlayerStatus = .empty
    @Published var reachable = false
    @Published var connecting = false

    // Volume shown/driven by the crown. Kept separate from status.volume so a rapid crown
    // turn isn't clobbered by the 1.5s status poll mid-adjust. The poll re-syncs it once the
    // user stops turning and the debounced send lands.
    @Published var volume: Int = 50
    private var volSendTask: Task<Void, Never>?
    private var adjustingVolume = false

    private var pollTask: Task<Void, Never>?
    private var base: URL? { ip.isEmpty ? nil : URL(string: "http://\(ip):8787") }

    var connected: Bool { !ip.isEmpty }

    func start() {
        pollTask?.cancel()
        guard base != nil else { return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                try? await Task.sleep(nanoseconds: 1_500_000_000)
            }
        }
    }

    func stop() { pollTask?.cancel(); pollTask = nil }

    func setIP(_ value: String) {
        ip = value.trimmingCharacters(in: .whitespaces)
        UserDefaults.standard.set(ip, forKey: "desktopIP")
        reachable = false
        start()
    }

    // Wipe the saved IP → returns to the connect screen (recover from a typo'd address).
    func forget() {
        stop()
        ip = ""
        UserDefaults.standard.removeObject(forKey: "desktopIP")
        status = .empty
        reachable = false
    }

    func refresh() async {
        guard let base else { return }
        do {
            var req = URLRequest(url: base.appendingPathComponent("status"))
            req.timeoutInterval = 4
            let (data, _) = try await URLSession.shared.data(for: req)
            let s = try JSONDecoder().decode(PlayerStatus.self, from: data)
            status = s
            reachable = true
            if !adjustingVolume { volume = s.volume }  // don't fight an in-progress crown turn
        } catch {
            reachable = false
        }
    }

    // ---- commands ----
    func playback(_ action: String) { post("playback/\(action)") }

    // Debounced: crown fires many times per turn. Update the shown value immediately, but only
    // POST the final level after the user pauses ~300ms, so the desktop isn't flooded.
    func setVolume(_ level: Int) {
        let v = max(0, min(100, level))
        volume = v
        adjustingVolume = true
        volSendTask?.cancel()
        volSendTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 300_000_000)
            if Task.isCancelled { return }
            await self?.sendVolume(v)
        }
    }

    private func sendVolume(_ v: Int) async {
        guard let base else { return }
        var req = URLRequest(url: base.appendingPathComponent("volume"))
        req.httpMethod = "POST"
        req.timeoutInterval = 4
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["level": v])
        _ = try? await URLSession.shared.data(for: req)
        adjustingVolume = false  // next poll may re-sync the shown value from the server
    }

    private func post(_ path: String, body: [String: Any]? = nil) {
        guard let base else { return }
        Task {
            var req = URLRequest(url: base.appendingPathComponent(path))
            req.httpMethod = "POST"
            req.timeoutInterval = 4
            if let body {
                req.setValue("application/json", forHTTPHeaderField: "Content-Type")
                req.httpBody = try? JSONSerialization.data(withJSONObject: body)
            }
            _ = try? await URLSession.shared.data(for: req)
            await refresh() // reflect the change quickly instead of waiting for the next poll
        }
    }
}

// Lenient decode — the desktop sends more fields than the watch needs; ignore the rest.
struct PlayerStatus: Decodable {
    var nowPlaying: NowPlaying?
    var isPlaying = false
    var isPaused = false
    var shuffle = false
    var repeatMode = "off"
    var volume = 50

    enum CodingKeys: String, CodingKey {
        case nowPlaying, isPlaying, isPaused, shuffle, volume
        case repeatMode = "repeat"
    }

    init() {}
    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        nowPlaying = try? c.decode(NowPlaying.self, forKey: .nowPlaying)
        isPlaying = (try? c.decode(Bool.self, forKey: .isPlaying)) ?? false
        isPaused = (try? c.decode(Bool.self, forKey: .isPaused)) ?? false
        shuffle = (try? c.decode(Bool.self, forKey: .shuffle)) ?? false
        repeatMode = (try? c.decode(String.self, forKey: .repeatMode)) ?? "off"
        volume = (try? c.decode(Int.self, forKey: .volume)) ?? 50
    }

    static let empty = PlayerStatus()
}

struct NowPlaying: Decodable {
    var title = ""
    var artist = ""
    var album = ""
    var name: String { title.isEmpty ? "—" : title }
}
