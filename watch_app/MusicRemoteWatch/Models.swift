import Foundation

/// Subset of GET /status the watch needs. Extra JSON keys are ignored by Codable.
struct NowPlaying: Codable {
    let title: String?
    let artist: String?
    let album: String?
}

struct PlayerStatus: Codable {
    let nowPlaying: NowPlaying?
    let isPlaying: Bool
    let volume: Int
}
