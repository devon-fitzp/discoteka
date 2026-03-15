# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## About

discoteka is a local music player (.NET 10, Avalonia UI) that imports library XML files from Apple Music/iTunes and Rekordbox, scans local files, and matches streaming tracks to local files on disk.

## Build & Run

```bash
# Build everything (from solution root)
dotnet build

# Run the GUI app (with console output)
cd discoteka && dotnet run

# Run the CLI tool
cd discoteka-cli && dotnet run -- <command>
```

**Linux requirement:** libVLC must be installed via your system package manager. On Windows it is bundled automatically via the `VideoLAN.LibVLC.Windows` NuGet package.

## CLI Commands (discoteka-cli)

```bash
dotnet run -- xml <path-to-xml>               # Import Apple Music or Rekordbox XML
dotnet run -- scan <path-to-music-folder>     # Scan filesystem for audio files
dotnet run -- clean [--confidence <0-100>] [--dry-run]   # Normalize/dedupe library
dotnet run -- match [--dry-run]               # Match TrackLibrary entries to local files
```

The CLI auto-detects XML format from the root element: `<plist>` → Apple Music, `<DJ_PLAYLISTS>` → Rekordbox.

## Project Structure

The solution has two projects:

- **discoteka-cli** — Class library + console entry point. Contains all database logic, importers, matching engine, and models. The GUI project references this as a `ProjectReference`.
- **discoteka** — Avalonia 11 desktop GUI app. MVVM with `CommunityToolkit.Mvvm`. References discoteka-cli for all data access.

### discoteka-cli key layers

| Path | Role |
|------|------|
| `Models/Track.cs` | All model types: `TrackLibraryTrack`, `AppleMusicTrack`, `RekordboxTrack`, `FileLibraryTrack`, plus index/metadata types |
| `Database/DatabaseInitializer.cs` | Creates/migrates the SQLite schema. Schema changes use `EnsureColumnExists` for additive migrations. |
| `Database/DbPaths.cs` | DB file lives at `%LOCALAPPDATA%/discoteka/discoteka.db` |
| `Database/TrackLibraryRepository.cs` | All read/write queries against the DB |
| `Database/TrackLibraryIndexBuilder.cs` | Rebuilds `TrackArtists`, `TrackAlbums`, `ArtistToAlbum`, `AlbumToTrack` index tables |
| `ImporterModules/AppleMusicLibrary.cs` | Parses Apple Music/iTunes plist XML |
| `ImporterModules/RekordboxLibrary.cs` | Parses Rekordbox XML |
| `ImporterModules/FileLibraryScanner.cs` | Walks filesystem, reads tags via TagLibSharp |
| `Utils/MatchEngine.cs` | Fuzzy-matches `AppleLibrary`/`Rekordbox` rows to `TrackLibrary` entries and local files, writes `TrackToFile`/`TrackToApple`/`TrackToRekordbox` join tables |
| `Utils/LibraryCleaner.cs` | Normalizes raw title/artist fields, deduplicates entries |
| `Jobs/BackgroundJobQueue.cs` | Thread-safe single-worker job queue |
| `Jobs/LibraryImportJobs.cs` | Wires import/scan operations into the background queue; after any import it automatically runs Normalize+Match+RebuildIndex |

### discoteka (GUI) key layers

| Path | Role |
|------|------|
| `ViewModels/MainWindowViewModel.cs` | Central view model. Manages library loading, sorting, filtering (`SmartFilterMode`), view modes (AllMusic/Artists/Albums), and delegates to `IMediaPlaybackService` and `ITrackLibraryRepository`. |
| `Playback/IMediaPlaybackService.cs` | Playback abstraction (queue, play/pause/seek, shuffle, repeat) |
| `Playback/MediaPlaybackService.cs` | LibVLCSharp implementation |
| `Playback/LibVlcNativeResolver.cs` | Resolves libVLC native binaries at runtime |
| `Views/MainWindow.axaml` | Main window XAML |
| `Views/JobOptionsDialog.axaml` | Dialog for triggering import/scan/clean/match jobs |

## Database Schema Overview

- **TrackLibrary** — canonical de-duplicated track list; the "source of truth" for the UI
- **AppleLibrary / Rekordbox / FileLibrary** — raw imported data from each source
- **TrackToApple / TrackToRekordbox / TrackToFile** — M:M join tables linking canonical tracks to source records
- **TrackArtists / TrackAlbums / ArtistToAlbum / AlbumToTrack** — pre-built index tables populated by `TrackLibraryIndexBuilder`; used for the Artists view
- **RecentActivity** — play history (inserted when a track crosses the 50% playback threshold)

Schema migrations are additive only: new columns are added via `EnsureColumnExists` which silently ignores duplicate-column errors.

## Architecture Notes

- The GUI never runs import/match logic inline — all heavy work goes through `IBackgroundJobQueue` so the UI thread stays responsive.
- After every import or scan job, a "Normalize + Match" job is automatically appended to the queue (confidence 0.45, auto-match score 0.92).
- `MainWindowViewModel` uses versioned load tokens (`_loadVersion`, `_artistIndexLoadVersion`) to discard stale async results when a newer load is already in flight.
- Playback play-count is recorded after the track crosses 50% of its duration, not on play start.
- The `discoteka` GUI project has `AvaloniaUseCompiledBindingsByDefault=true` — all XAML bindings are compiled; types must be properly declared.
