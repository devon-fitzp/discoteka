# v1 Feature Build Plan

## Goals
- Ship a functional, cross-platform local media player in the existing Avalonia app.
- Reuse current player UI controls and evolve them into production behavior.
- Support playback flows from track list, artist/album views, and smart filters.
- Capture foundational listening activity data for future recommendations/features.
- Keep architecture extensible for weighted shuffle and richer playback logic later.

## Scope Summary
This v1 includes:
- LibVLCSharp-based playback engine.
- Track double-click playback.
- Queue progression to next visible/active track.
- Shuffle and repeat modes.
- 50% play-count increment rule.
- Recent activity persistence (`TrackId`, timestamp).
- Smart filters for local availability.
- New library views: `Artists` (drill-down) and `Albums` (grid + expand).

Out-of-scope for v1:
- Cloud/streaming playback.
- Advanced queue editing UI.
- Weighted shuffle tuning (only architecture hook now).
- External cover-art fetching pipeline.

## Stage 1: Dependencies and Playback Abstraction
### Deliverables
- Add NuGet packages for VLC playback:
  - `LibVLCSharp`
  - `LibVLCSharp.Avalonia`
  - Platform runtime package strategy (`VideoLAN.LibVLC.*`) per release target.
- Startup initialization for LibVLC in app lifecycle.
- Create playback abstraction so view models do not directly depend on VLC APIs.

### Design
- Introduce `IMediaPlaybackService` interface:
  - `Play(track)`
  - `Pause()`
  - `Resume()`
  - `Stop()`
  - `Seek(position)`
  - `SetVolume(volume)`
  - `PlayNext()` / `PlayPrevious()`
  - `SetShuffle(enabled)`
  - `SetRepeatMode(mode)`
- Expose events/state:
  - current track changed
  - position/duration updates
  - playback ended
  - playback errors
  - queue changed

### Acceptance Criteria
- App starts with LibVLC initialized.
- No playback logic directly embedded in UI code-behind.

## Stage 2: Data Model and Persistence Updates
### Deliverables
- Add `RecentActivity` table migration.
- Add repository methods for play count and activity insertion.
- Ensure track rows expose stable `TrackId` and `FilePath` for playback.

### DB changes
- New table:
  - `RecentActivity`
    - `ActivityId INTEGER PRIMARY KEY`
    - `TrackId INTEGER NOT NULL`
    - `PlayedAtUtc TEXT NOT NULL` (ISO-8601 UTC)
- Optional index:
  - `IX_RecentActivity_TrackId_PlayedAtUtc`

### Repository additions
- `IncrementPlayCount(long trackId)`
- `InsertRecentActivity(long trackId, DateTime playedAtUtc)`
- Query helpers for:
  - local availability
  - artists/albums groupings
  - album tracks

### Acceptance Criteria
- Migration runs safely on existing DBs.
- Can persist both play count increments and activity records.

## Stage 3: Core Queue + Now Playing Session
### Deliverables
- Implement `PlaybackQueue` built from current visible track context.
- Track double-click starts playback at clicked row.
- End-of-track advances to next playable track.

### Queue behavior
- Source of truth is current view/filter result at playback start.
- Queue stores ordered track references (`TrackId`, path, metadata snapshot).
- If track has no local file or file missing:
  - manual play: show popup (`No local file!`)
  - auto-next: skip silently

### Acceptance Criteria
- Double-clicking a row plays it.
- Finishing a track advances according to queue order.
- Non-playable tracks are skipped correctly.

## Stage 4: Reuse and Complete Existing Player Controls
### Deliverables
- Wire current bottom controls (`Prev`, `Play/Pause`, `Next`, seek slider, volume slider).
- Bind “now playing” labels to live state.
- Keep controls functional regardless of source view (All Music/Artists/Albums).

### UI specifics
- Play button toggles pause/resume for current item.
- Seek and volume are reflected immediately in playback engine.
- Status message updates for key player states.

### Acceptance Criteria
- Existing controls are fully functional and stateful.

## Stage 5: Repeat + Shuffle Controls and Rules
### Deliverables
- Add Shuffle toggle (`On/Off`).
- Add Repeat mode cycle (`Off -> Track -> Playlist -> Off`).
- Add explicit next-track chooser extension point.

### Engine rules
- `Repeat Track`: replay same track on end.
- Else if `Shuffle On`: call `ShuffleChooseNextTrack(...)`.
- Else: linear next.
- At queue end + `Repeat Playlist`: wrap to queue start.

### Extension hook
- Implement `ShuffleChooseNextTrack(...)` now with PRNG.
- Keep function isolated for future weighted logic (e.g., avoid same-artist adjacency).

### Acceptance Criteria
- User can toggle repeat/shuffle and observe correct behavior.
- Shuffle logic is centralized behind a replaceable function.

## Stage 6: 50% Play Rule + Listening Activity
### Deliverables
- Playback session tracker that marks when 50% threshold is crossed.
- Persist once per track-play session:
  - `TrackLibrary.Plays += 1`
  - `RecentActivity` insert with UTC timestamp

### Edge handling
- Seeks should not produce duplicate increments.
- Pause/resume should not reset 50% eligibility.
- Replaying the same track as a fresh playback session can count again.

### Acceptance Criteria
- Track play count increments only when >50% was played.
- `RecentActivity` records are written exactly once per counted play.

## Stage 7: Smart Filters for Local Availability
### Deliverables
- Add/complete smart filters:
  - `Available Locally`
  - `No Local File`
- `Available Locally` means path exists and file is playable.
- `No Local File` means no local path or file missing.

### Behavior
- Filter selection updates current list view and queue source.
- Attempting to play item from `No Local File` list triggers popup.

### Acceptance Criteria
- Filters return correct dataset and integrate with playback logic.

## Stage 8: Library Navigation and View Modes
### Deliverables
- Rationalize left-nav:
  - Keep `All Music`
  - Remove redundant `Tracks` item (or alias to All Music)
  - Keep `Artists`
  - Keep `Albums`
- Introduce view mode state in VM/UI.

### Acceptance Criteria
- App can switch among `All Music`, `Artists`, and `Albums` views.
- Active view drives what is rendered and playable.

## Stage 9: Artists View (Drill-Down)
### Deliverables
- Artist list with caret expand/collapse.
- Expanded artist row shows horizontal album tiles.
- Album tile click toggles a track pane for that album.

### Artwork
- Use blank thumbnails by default.
- If embedded local metadata artwork is easy/cheap to access, optionally surface it.

### Playback actions
- Allow playing an album from artist-expanded context.

### Acceptance Criteria
- Users can browse artist -> album -> tracks hierarchically.
- Expand/collapse behavior is stable and performant.

## Stage 10: Albums View (Grid + Expand)
### Deliverables
- Grid of albums.
- Click album tile to expand/collapse its track list pane.
- Hover album art shows play icon/button.
- Clicking play on album queues that album and starts playback.

### Acceptance Criteria
- Album grid interaction works smoothly.
- Album playback starts from first playable track in album context.

## Stage 11: UX Rules and Error Handling
### Deliverables
- Message box on manual play with no local file:
  - title/content: `No local file!`
  - `OK` action
- Auto-skip unplayable items without blocking dialogs.
- Clear status messaging for skip/end/empty-queue states.

### Acceptance Criteria
- Missing-file behavior matches requested UX exactly.

## Stage 11.5: Player Layout and Micro-Interaction Polish
### Deliverables
- Move status text out of the now-playing area and into the top app bar.
- Refactor the lower player bar into stable zones with explicit width allocations:
  - Now Playing pane: fixed width target of ~25-30% of the lower bar.
  - Transport/seek pane: center zone.
  - Volume pane: right zone.
- Prevent now-playing pane width from changing with title length.

### Now Playing pane requirements
- Show metadata on three lines:
  - `Title`
  - `Artist`
  - `Album`
- Increase album art size modestly by reclaiming space from horizontal text layout.
- Handle long text with marquee behaviors:
  - slightly-overflowing text: smooth side-to-side scroll.
  - very long text: typewriter-style loop (scroll/fade/reset).

### Transport and timing requirements
- Center duration indicators under the seek/progress bar.
- Add horizontal padding on both ends of the seek bar (do not stretch edge-to-edge).
- Keep seek bar visually aligned with the volume slider area.
- Ensure seek bar supports manual scrubbing/seek input without snapping back to current playback position (update binding/event strategy so user drag commits properly).

### Volume pane requirements
- Move `Volume` label above the slider.
- Add internal horizontal padding to the volume control area.

### Acceptance Criteria
- Player bar has stable, non-jittering layout regardless of metadata length.
- Long metadata remains readable without breaking layout.
- Timing and slider alignment is visually consistent across common window widths.

## Stage 12: Validation, Testing, and v1 Exit Criteria
### Manual test matrix
- Double-click playback from All Music.
- Prev/Next/Play/Pause/Seek/Volume behavior.
- End-of-track auto-next.
- Shuffle on/off behavior and deterministic fallback handling.
- Repeat modes (`Track`, `Playlist`, `Off`).
- 50% rule increments plays correctly.
- `RecentActivity` rows inserted with accurate timestamps.
- `Available Locally` and `No Local File` filter correctness.
- Artists and Albums view drill-down and album play interactions.
- Player bar layout stability with short/long metadata and resize scenarios.
- Marquee behavior quality checks for title/artist/album overflow cases.

### Reliability checks
- App behavior when queue has only unplayable tracks.
- Playback service disposal/cleanup on app exit.
- Cross-platform smoke checks (Linux/Windows minimum, macOS if targeted).

### v1 release readiness criteria
- All core playback flows work without crashes.
- Data persistence (plays + recent activity) verified.
- View navigation and smart filters functionally complete.
- No blockers in known issues list.

## Suggested Implementation Order (Execution Roadmap)
1. Stage 1-2 (foundation and persistence)
2. Stage 3-4 (queue + core controls)
3. Stage 5-6 (shuffle/repeat + 50% activity)
4. Stage 7-8 (filters + view mode framework)
5. Stage 9-10 (Artists/Albums UI)
6. Stage 11-12 (polish, validation, release gate)

## Risks and Mitigations
- VLC runtime packaging differences by OS.
  - Mitigation: define per-platform packaging strategy early and smoke test.
- UI complexity growth from multi-view navigation.
  - Mitigation: centralize state in view model and keep view components modular.
- Inaccurate play counting from seek edge-cases.
  - Mitigation: explicit per-session threshold flag + test scenarios.
- Performance concerns for large libraries in grouped views.
  - Mitigation: incremental loading/virtualization plan if needed after first pass.

## Future Hooks (Post-v1)
- Weighted shuffle engine (artist/album separation heuristics).
- Recently Played smart filter/view powered by `RecentActivity`.
- Rich queue UI (reorder, remove, enqueue next).
- Cover art caching/fetch pipeline.
- Advanced recommendations and listening analytics.
