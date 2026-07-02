import Foundation

/// Talks to the Music Organiser desktop control API over the LAN (same endpoints the
/// phone app uses). Independent of the phone: the watch hits the host directly.
@MainActor
final class PlayerClient: ObservableObject {
    @Published var host: String { didSet { UserDefaults.standard.set(host, forKey: "host") } }
    @Published var port: String { didSet { UserDefaults.standard.set(port, forKey: "port") } }

    @Published var status: PlayerStatus?
    @Published var reachable = false
    @Published var volume: Double = 50
    /// True while the user drags the volume slider, so polling doesn't fight the value.
    var adjustingVolume = false

    private var timer: Timer?
    private var phoneLink: PhoneLink?

    init() {
        host = UserDefaults.standard.string(forKey: "host") ?? ""
        port = UserDefaults.standard.string(forKey: "port") ?? "8787"

        // Auto-configure from the paired iPhone when it pushes the desktop host:port.
        phoneLink = PhoneLink { host, port in
            Task { @MainActor [weak self] in
                guard let self else { return }
                let changed = host != self.host || (!port.isEmpty && port != self.port)
                self.host = host
                if !port.isEmpty { self.port = port }
                if changed { self.start() }
            }
        }
    }

    private func url(_ path: String) -> URL? {
        guard !host.isEmpty, Int(port) != nil else { return nil }
        return URL(string: "http://\(host):\(port)/\(path)")
    }

    // MARK: polling

    func start() {
        stop()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { await self?.refresh() }
        }
        Task { await refresh() }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    func refresh() async {
        guard let u = url("status") else { reachable = false; return }
        do {
            let (data, resp) = try await URLSession.shared.data(from: u)
            guard (resp as? HTTPURLResponse)?.statusCode == 200 else { reachable = false; return }
            let s = try JSONDecoder().decode(PlayerStatus.self, from: data)
            status = s
            reachable = true
            if !adjustingVolume { volume = Double(s.volume) }
        } catch {
            reachable = false
        }
    }

    // MARK: commands

    private func post(_ path: String, body: [String: Any]? = nil) {
        guard let u = url(path) else { return }
        var req = URLRequest(url: u)
        req.httpMethod = "POST"
        if let body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        }
        Task {
            _ = try? await URLSession.shared.data(for: req)
            await refresh()
        }
    }

    func playPause() { post("playback/playpause") }
    func next()      { post("playback/next") }
    func previous()  { post("playback/previous") }
    func setVolume(_ level: Int) { post("volume", body: ["level": level]) }
}
