# Changelog

## 2026-10-22
- New "Stop after current track" button between Stop and Next: the current track plays to its end and then playback stops (one-shot, overrides repeat one and auto-advance)
- Shuffle and repeat are now icon dropdowns: click for a menu (Shuffle: Off / least played first; Repeat: Off / all / one) with the current mode checked
- Karaoke button moved to the now-playing row next to the rating
- Waveform style dropdown moved under the seek bar and made smaller; transport buttons sit closer to the bar
- HTTP API: `POST /playback/stop-after-current` toggles it, `/status` reports `stopAfterCurrent`
- HTTP API: `POST /playback/shuffle` takes optional `{enabled}` and `POST /playback/repeat` optional `{mode}` to set the mode directly (no body still toggles / cycles)
- Mobile: "Stop after" toggle next to Shuffle and Repeat (also in the offline player); Shuffle and Repeat open a menu like on the desktop; the lyrics toggle moved next to the rating
- Mobile: the four waveform fingerprint styles (Ribbons, Strands, BeatBars, Spectrum), picked from a small dropdown under the seek bar and remembered per phone

## 2026-10-21
- Legacy-codepage mp3 tags no longer show mojibake: Cyrillic (Windows-1251), Polish (Windows-1250) and Windows-1252 punctuation (a `0x92` byte where an apostrophe belongs), in ID3v1 and in ID3v2 tags mislabelled as Latin-1 (`LegacyTagEncoding`). The codepage is decided once per file, so short fields follow their Cyrillic siblings. Previously cached garbled tags are re-read on next folder visit. mp3 only; no KOI8-R/CP866/CJK

## 2026-10-10
- Seek bar animation no longer burns CPU while playing: the chart is drawn once and only the playhead layer updates (was 11-18% of total CPU on 8 cores in the new styles, now about 2-3%)
- Waveform seek bar: four new "track fingerprint" styles next to the original bars, switchable from a dropdown in the transport bar - Ribbons, Strands, Beat-width bars and Spectrum. They combine loudness, frequency content and tempo so a fast bass-heavy track, a slow heavy one and a light one look different at a glance. The analysis now also estimates tempo and beat strength per section and an 8-band spectrum (`GET /file/waveform` returns `tempo`, `beat`, `spectrum`)

## 2026-10-09
- Waveform seek bar: the position bar now draws the whole track as a loudness chart (quiet and loud parts visible at a glance), with click and drag to seek. Computed once in the background, cached in the DB, precomputed for the next track; long tracks fill in progressively
- Waveform bars are frequency-coloured (pink = bass-heavy, violet = mids, cyan = treble, relative to what is usual for the track); the desktop bar also pulses live at the playhead - bars swell with loudness and the playhead glows on kick hits
- FLAC seeking is now exact: the app picks the FLAC frame itself instead of trusting the system decoder, which landed up to ~10 s away from the requested position while the playhead claimed otherwise
- The seek bar playhead and elapsed time now follow what is actually audible (device clock) instead of the file read position, which ran about 1.5 s ahead of the sound
- Waveform heights use a per-track decibel contrast stretch, so a passage only 2-3 dB quieter is clearly visible
- `/status` reports `readPositionMs` and `audiblePositionMs`
- HTTP API: `GET /file/waveform`; mobile Now Playing shows the same bar (falls back to the slider while loading)
- Folder copy/move: when the destination already holds the same or a similarly named folder (case, punctuation, years and `(...)`/`[...]` parts ignored), a FreeFileSync-style comparison dialog opens instead of silently renaming to `Name (1)`. Source and target are paired row by row; each file or folder gets its own action (`→` bring in / overwrite, `–` skip, `✕` delete from target), with **Merge** and **Replace** as presets, a live summary, **Keep both** under an editable name, and **Into subfolder** (the folder lands inside the existing one, e.g. an album into its band folder). Target folders with no counterpart in the source are hidden and never touched. Files and folders can be renamed in place (F2 / double-click). Covers the context menu, recent folders and drag & drop
- Busy overlay during folder copy / move / delete; the phone shows a spinner during folder moves
- HTTP API: `GET /folder/similar`, and `POST /folder/move` takes optional `action` (`merge`/`replace`/`keep`/`into`), `target`, `newName`
- Mobile: folder Move shows the same choice when a similar folder exists

## 2026-10-06
- Audio tools: **Convert to MP3** (320k, tags/cover copied, never overwrites, source kept) and **Split by .cue** (album image to sample-exact lossless `NN - Title.flac` tracks tagged from the cue; cue auto-located or picked) in the grid context menu - background job with a progress overlay, new files appear without rebuilding the queue
- HTTP API: `POST /file/convert-mp3` and `POST /file/split-cue` (block until done)
- Mobile: Convert to MP3 and Split by .cue in the track menu

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
