import SwiftUI

struct SettingsView: View {
    @ObservedObject var client: PlayerClient
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        Form {
            Section("Desktop") {
                TextField("Host (e.g. 192.168.1.20)", text: $client.host)
                TextField("Port", text: $client.port)
            }
            Section {
                Button("Save") {
                    client.start()
                    dismiss()
                }
                HStack {
                    Circle()
                        .fill(client.reachable ? .green : .secondary)
                        .frame(width: 8, height: 8)
                    Text(client.reachable ? "Connected" : "Not connected")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle("Connection")
    }
}
