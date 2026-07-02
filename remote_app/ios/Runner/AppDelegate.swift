import Flutter
import UIKit
import WatchConnectivity

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  private var watchChannel: FlutterMethodChannel?

  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    activateWatchSession()
    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)

    // Dart -> native: forward the current host:port to the paired Apple Watch.
    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "WatchLink") {
      let channel = FlutterMethodChannel(name: "music_organiser/watch",
                                         binaryMessenger: registrar.messenger())
      channel.setMethodCallHandler { [weak self] call, result in
        guard call.method == "sendHost",
              let args = call.arguments as? [String: Any] else {
          result(FlutterMethodNotImplemented)
          return
        }
        let host = args["host"] as? String ?? ""
        let port = args["port"] as? Int ?? 8787
        self?.sendToWatch(host: host, port: port)
        result(true)
      }
      watchChannel = channel
    }
  }

  private func activateWatchSession() {
    guard WCSession.isSupported() else { return }
    let session = WCSession.default
    session.delegate = self
    session.activate()
  }

  /// updateApplicationContext keeps only the latest value and delivers it to the watch
  /// at the next opportunity — so one call is enough even if the watch app is closed.
  private func sendToWatch(host: String, port: Int) {
    guard WCSession.isSupported() else { return }
    let session = WCSession.default
    guard session.activationState == .activated else { return }
    try? session.updateApplicationContext(["host": host, "port": port])
  }
}

extension AppDelegate: WCSessionDelegate {
  func session(_ session: WCSession,
               activationDidCompleteWith activationState: WCSessionActivationState,
               error: Error?) {}
  func sessionDidBecomeInactive(_ session: WCSession) {}
  func sessionDidDeactivate(_ session: WCSession) {
    // Re-activate so a switched watch keeps receiving context.
    WCSession.default.activate()
  }
}
