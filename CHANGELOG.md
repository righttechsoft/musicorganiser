# Changelog

## 2026-09-03
- Grid: text + star-rating filter over the loaded folder (in-memory)
- Folder tree: rating filter, DB-driven, mirrors the existing tag filter
- File moves now route through `MainViewModel.MoveFileAsync` (mirrors the delete path) - drag & drop, the artist-folder helpers, and the mobile API all keep the queue playing through a move instead of stopping it
- Grid recenters on the playing row on every track change, not just on shuffle
- Copy-path added to file/folder context menus

## 2026-08-19
**Desktop**
- Lyrics: LRCLIB -> LRCMux -> SyncLRC, then AI web search as a last resort; karaoke big view; time-proportional sync editor with hand-timing; Whisper.net alignment for plain-only lyrics
- Folder tags: AI suggestions (right-click -> *Tag...*), bulk-tag subfolders with a per-band preview player, tag management (delete/merge), DB-driven tag filter in the tree
- File properties dialog
- Drag & drop tracks or folders onto a tree folder (Ctrl = copy); New subfolder from the folder context menu
- Deleting the playing track now advances to the next track instead of stopping playback
- Session restore: last folder reopens, last played track is reselected and scrolled into view, ready for Play to resume it
- Grid rows: pinned row height (hover editors no longer nudge the list); fixed a rendering bug where the selected row's cell trigger painted white text on an untinted background, making it look blank

**Mobile**
- Karaoke lyrics view
- Folder tagging, bulk tag, and tag management; tag filter
- Copy to... alongside Move to...
- Recursive file listing for the band preview player
- Album art cycling on Now Playing
