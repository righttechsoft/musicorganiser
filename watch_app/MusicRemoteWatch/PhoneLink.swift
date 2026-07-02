import Foundation
import WatchConnectivity

/// Receives the desktop host:port pushed from the iPhone app via WatchConnectivity,
/// so the watch auto-configures without manual entry. Callback runs on the main queue.
final class PhoneLink: NSObject, WCSessionDelegate {
    private let onHost: (String, String) -> Void

    init(onHost: @escaping (String, String) -> Void) {
        self.onHost = onHost
        super.init()
        guard WCSession.isSupported() else { return }
        let session = WCSession.default
        session.delegate = self
        session.activate()
    }

    private func apply(_ context: [String: Any]) {
        let host = context["host"] as? String ?? ""
        // Port may arrive as Int or String depending on the sender; accept both.
        let port: String
        if let p = context["port"] as? Int { port = String(p) }
        else { port = context["port"] as? String ?? "" }
        guard !host.isEmpty else { return }
        onHost(host, port) // consumer hops to the main actor
    }

    // Delivered while running.
    func session(_ session: WCSession, didReceiveApplicationContext applicationContext: [String: Any]) {
        apply(applicationContext)
    }

    // On activation, pick up any context the phone sent before the watch app launched.
    func session(_ session: WCSession,
                 activationDidCompleteWith activationState: WCSessionActivationState,
                 error: Error?) {
        let existing = session.receivedApplicationContext
        if !existing.isEmpty { apply(existing) }
    }
}
