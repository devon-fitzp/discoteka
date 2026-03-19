using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Discoteka.Core.Database;
using Discoteka.Core.Models;

namespace Discoteka.Desktop.ViewModels;

/// <summary>
/// Owns track list loading, sort, and filter state. All data work lives here;
/// status messaging and view-mode coordination remain in <see cref="MainWindowViewModel"/>.
/// </summary>
public sealed class LibraryViewModel : ViewModelBase
{
    private enum SortColumn { None, Title, Artist, Album, Time, Genre, Formats, Plays }
    private enum SortDirection { None, Ascending, Descending }

    private readonly ITrackLibraryRepository _repository;
    private readonly List<TrackRowViewModel> _libraryRows = new();
    private readonly List<TrackRowViewModel> _allTracks = new();

    // Shared raw DB rows cache — used by both ArtistsBrowserViewModel and AlbumsBrowserViewModel
    private IReadOnlyList<IndexedArtistAlbumEntry>? _cachedIndexedRows;
    private bool? _cachedIndexedRowsRequireLocalFile;
    private int _loadVersion;
    private int _trackCount;
    private DefaultSortOption _defaultSort = DefaultSortOption.Title;
    private SmartFilterMode _smartFilterMode = SmartFilterMode.None;
    private SortColumn _activeSortColumn = SortColumn.None;
    private SortDirection _activeSortDirection = SortDirection.None;

    public LibraryViewModel(ITrackLibraryRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Fired on the UI thread after <see cref="RebuildVisibleTracks"/> completes.</summary>
    public event Action? TracksReloaded;

    public BulkObservableCollection<TrackRowViewModel> Tracks { get; } = new();
    public string TrackCountText => _trackCount == 1 ? "1 track" : $"{_trackCount} tracks";
    public SmartFilterMode CurrentSmartFilter => _smartFilterMode;

    public async Task LoadTracksAsync()
    {
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        Console.WriteLine($"[Library] Load {loadVersion} started.");
        var tracks = await _repository.GetAllAsync();
        var rows = tracks.Select(MapTrack).ToList();
        Console.WriteLine($"[Library] Load {loadVersion} fetched {rows.Count} row(s).");

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loadVersion != Volatile.Read(ref _loadVersion))
                {
                    Console.WriteLine($"[Library] Load {loadVersion} skipped (newer load exists).");
                    return;
                }

                _libraryRows.Clear();
                _libraryRows.AddRange(rows);
                _allTracks.Clear();
                RebuildVisibleTracks();
                Console.WriteLine($"[Library] Load {loadVersion} applied to UI.");
            });
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[Library] Load {loadVersion} cancelled (dispatcher shutting down).");
        }
    }

    public void SetDefaultSort(DefaultSortOption option)
    {
        _defaultSort = option;
        _activeSortColumn = SortColumn.None;
        _activeSortDirection = SortDirection.None;
        ApplyCurrentSort();
    }

    public void SetSmartFilter(SmartFilterMode mode)
    {
        if (_smartFilterMode == mode)
        {
            return;
        }

        _smartFilterMode = mode;
        Console.WriteLine($"[Library] Smart filter set to {_smartFilterMode}.");
        RebuildVisibleTracks();
    }

    public void CycleSortByTitle() => CycleSort(SortColumn.Title);
    public void CycleSortByArtist() => CycleSort(SortColumn.Artist);
    public void CycleSortByAlbum() => CycleSort(SortColumn.Album);
    public void CycleSortByTime() => CycleSort(SortColumn.Time);
    public void CycleSortByGenre() => CycleSort(SortColumn.Genre);
    public void CycleSortByFormats() => CycleSort(SortColumn.Formats);
    public void CycleSortByPlays() => CycleSort(SortColumn.Plays);

    /// <summary>
    /// Returns the local-file filter value to pass to indexed DB queries, derived from the current smart filter.
    /// <c>true</c> = require local file, <c>false</c> = require no local file, <c>null</c> = no constraint.
    /// </summary>
    public bool? MapSmartFilterToIndexedQuery()
    {
        return _smartFilterMode switch
        {
            SmartFilterMode.AvailableLocally => true,
            SmartFilterMode.NoLocalFile => false,
            _ => null
        };
    }

    public Task<TrackMetadataSnapshot?> GetTrackMetadataSnapshotAsync(long trackId, CancellationToken cancellationToken = default)
        => _repository.GetTrackMetadataSnapshotAsync(trackId, cancellationToken);

    public Task SaveTrackMetadataTabAsync(MetadataTabEntry tab, CancellationToken cancellationToken = default)
        => _repository.SaveMetadataTabAsync(tab, cancellationToken);

    public Task<IReadOnlyList<TrackLibraryTrack>> GetIndexedAlbumTracksAsync(long albumId, bool? requireLocalFile, CancellationToken cancellationToken = default)
        => _repository.GetIndexedAlbumTracksAsync(albumId, requireLocalFile, cancellationToken);

    /// <summary>
    /// Returns artist/album index rows, using a per-filter cache to avoid redundant DB queries
    /// when both the Artists and Albums browsers need the same data.
    /// Call <see cref="InvalidateIndexedRowsCache"/> whenever underlying data changes.
    /// </summary>
    public async Task<IReadOnlyList<IndexedArtistAlbumEntry>> GetCachedIndexedRowsAsync()
    {
        var requireLocalFile = MapSmartFilterToIndexedQuery();
        if (_cachedIndexedRows != null && _cachedIndexedRowsRequireLocalFile == requireLocalFile)
        {
            return _cachedIndexedRows;
        }

        var rows = await _repository.GetIndexedArtistAlbumsAsync(requireLocalFile);
        _cachedIndexedRows = rows;
        _cachedIndexedRowsRequireLocalFile = requireLocalFile;
        return rows;
    }

    /// <summary>Clears the shared indexed rows cache. Call after any import/scan job completes.</summary>
    public void InvalidateIndexedRowsCache()
    {
        _cachedIndexedRows = null;
        _cachedIndexedRowsRequireLocalFile = null;
    }

    public Task IncrementPlayCountAsync(long trackId, CancellationToken cancellationToken = default)
        => _repository.IncrementPlayCountAsync(trackId, cancellationToken);

    public Task InsertRecentActivityAsync(long trackId, DateTime timestamp, CancellationToken cancellationToken = default)
        => _repository.InsertRecentActivityAsync(trackId, timestamp, cancellationToken);

    internal static TrackRowViewModel MapTrack(TrackLibraryTrack track)
    {
        var trackId = 0L;
        if (!string.IsNullOrWhiteSpace(track.TrackId))
        {
            long.TryParse(track.TrackId, out trackId);
        }

        var title = string.IsNullOrWhiteSpace(track.TrackTitle) ? "Untitled" : track.TrackTitle!;
        var artist = string.IsNullOrWhiteSpace(track.TrackArtist) ? "Unknown Artist" : track.TrackArtist!;
        var album = string.IsNullOrWhiteSpace(track.AlbumTitle) ? "Unknown Album" : track.AlbumTitle!;
        var genre = string.IsNullOrWhiteSpace(track.Genre) ? "-" : track.Genre!;

        var durationText = "-";
        if (track.Duration.HasValue)
        {
            var span = TimeSpan.FromSeconds(track.Duration.Value);
            durationText = span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }

        var plays = track.Plays;
        var playsText = plays.HasValue ? plays.Value.ToString() : "-";
        var formats = "-";
        if (!string.IsNullOrWhiteSpace(track.FilePath))
        {
            var ext = Path.GetExtension(track.FilePath).TrimStart('.').ToUpperInvariant();
            formats = string.IsNullOrWhiteSpace(ext) ? "-" : ext;
        }

        var subtitle = FormatSubtitle(track.DjTags);
        return new TrackRowViewModel(trackId, title, subtitle, artist, album, track.TrackNumber, durationText, track.Duration, genre, formats, playsText, plays, track.FilePath);
    }

    internal static bool HasLocalFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private void CycleSort(SortColumn column)
    {
        var firstDirection = column == SortColumn.Plays ? SortDirection.Descending : SortDirection.Ascending;
        var secondDirection = firstDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;

        if (_activeSortColumn != column)
        {
            _activeSortColumn = column;
            _activeSortDirection = firstDirection;
        }
        else if (_activeSortDirection == firstDirection)
        {
            _activeSortDirection = secondDirection;
        }
        else if (_activeSortDirection == secondDirection)
        {
            _activeSortColumn = SortColumn.None;
            _activeSortDirection = SortDirection.None;
        }
        else
        {
            _activeSortDirection = firstDirection;
        }

        ApplyCurrentSort();
    }

    private void ApplyCurrentSort()
    {
        IEnumerable<TrackRowViewModel> sorted;
        if (_activeSortColumn == SortColumn.None || _activeSortDirection == SortDirection.None)
        {
            sorted = ApplyDefaultSort(_allTracks);
        }
        else
        {
            sorted = ApplyColumnSort(_allTracks, _activeSortColumn, _activeSortDirection);
        }

        Tracks.ResetWith(sorted);
        _trackCount = Tracks.Count;
        OnPropertyChanged(nameof(TrackCountText));
    }

    private void RebuildVisibleTracks()
    {
        IEnumerable<TrackRowViewModel> filtered = _libraryRows;
        filtered = _smartFilterMode switch
        {
            SmartFilterMode.AvailableLocally => filtered.Where(row => HasLocalFile(row.FilePath)),
            SmartFilterMode.NoLocalFile => filtered.Where(row => !HasLocalFile(row.FilePath)),
            _ => filtered
        };

        _allTracks.Clear();
        _allTracks.AddRange(filtered);
        ApplyCurrentSort();
        TracksReloaded?.Invoke();
    }

    private IEnumerable<TrackRowViewModel> ApplyDefaultSort(IEnumerable<TrackRowViewModel> rows)
    {
        return _defaultSort switch
        {
            DefaultSortOption.Title => rows
                .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Artist, StringComparer.OrdinalIgnoreCase),
            DefaultSortOption.Artist => rows
                .OrderBy(row => row.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase),
            DefaultSortOption.RecentlyAdded => rows
                .OrderByDescending(row => row.TrackId),
            _ => rows
        };
    }

    private static IEnumerable<TrackRowViewModel> ApplyColumnSort(IEnumerable<TrackRowViewModel> rows, SortColumn column, SortDirection direction)
    {
        return column switch
        {
            SortColumn.Title => Order(rows, row => row.Title, direction),
            SortColumn.Artist => Order(rows, row => row.Artist, direction),
            SortColumn.Album => Order(rows, row => row.Album, direction),
            SortColumn.Time => Order(rows, row => row.DurationSeconds ?? int.MinValue, direction),
            SortColumn.Genre => Order(rows, row => row.Genre, direction),
            SortColumn.Formats => Order(rows, row => row.Formats, direction),
            SortColumn.Plays => Order(rows, row => row.Plays ?? int.MinValue, direction),
            _ => rows
        };
    }

    private static IOrderedEnumerable<TrackRowViewModel> Order<T>(IEnumerable<TrackRowViewModel> rows, Func<TrackRowViewModel, T> keySelector, SortDirection direction)
    {
        return direction == SortDirection.Descending
            ? rows.OrderByDescending(keySelector).ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(keySelector).ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatSubtitle(string? djTagsJson)
    {
        if (string.IsNullOrWhiteSpace(djTagsJson))
        {
            return "-";
        }

        try
        {
            var tags = JsonSerializer.Deserialize<string[]>(djTagsJson);
            if (tags == null || tags.Length == 0)
            {
                return "-";
            }

            var values = tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .ToArray();
            return values.Length == 0 ? "-" : string.Join(", ", values);
        }
        catch
        {
            return djTagsJson;
        }
    }
}
