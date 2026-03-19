using Discoteka.Core.Database;
using Discoteka.Core.ImporterModules;
using Discoteka.Core.Utils;

namespace Discoteka.Core.Jobs;

/// <summary>
/// Factory and coordinator for all library-related background jobs.
/// Every method enqueues work into the shared <see cref="IBackgroundJobQueue"/>
/// without blocking the caller.
/// </summary>
public interface ILibraryImportJobs
{
    /// <summary>Imports an Apple Music or iTunes XML file into <c>AppleLibrary</c>.</summary>
    ValueTask QueueAppleMusicImportAsync(string xmlPath, CancellationToken cancellationToken = default);

    /// <summary>Recursively scans <paramref name="rootPath"/> for audio files and imports them into <c>FileLibrary</c>.</summary>
    ValueTask QueueMediaScanAsync(string rootPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs LibraryCleaner and MatchEngine with the given thresholds.
    /// Clamp <paramref name="minConfidence"/> to [0, 1] before passing.
    /// </summary>
    ValueTask QueueCleanupAsync(double minConfidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs MatchEngine with the given minimum auto-match score.
    /// Useful after manual library edits.
    /// </summary>
    ValueTask QueueMatchRescanAsync(double minScore, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the standard post-import pipeline: LibraryCleaner (confidence ≥ 0.45)
    /// → MatchEngine (auto-score ≥ 0.92) → RebuildIndex.
    /// </summary>
    ValueTask QueueNormalizeAndMatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds <c>TrackArtists</c>, <c>TrackAlbums</c>, <c>ArtistToAlbum</c>, and <c>AlbumToTrack</c>
    /// from the current state of <c>TrackLibrary</c>.
    /// </summary>
    ValueTask QueueRebuildIndexAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ILibraryImportJobs"/> implementation.
/// <para>
/// Import and scan jobs automatically chain a "Normalize + Match" job afterward,
/// so the library is always in a consistent matched/indexed state after any data ingestion.
/// </para>
/// <para>
/// Hard-coded pipeline thresholds:
/// <list type="bullet">
///   <item>LibraryCleaner minimum confidence: <c>0.45</c></item>
///   <item>MatchEngine minimum auto-match score: <c>0.92</c></item>
/// </list>
/// These were tuned empirically; adjust in <see cref="BuildNormalizeAndMatchJob"/> if needed.
/// </para>
/// </summary>
public sealed class LibraryImportJobs : ILibraryImportJobs
{
    private readonly IBackgroundJobQueue _queue;
    private readonly string? _dbPath;

    public LibraryImportJobs(IBackgroundJobQueue queue, string? dbPath = null)
    {
        _queue = queue;
        _dbPath = dbPath;
    }

    public ValueTask QueueAppleMusicImportAsync(string xmlPath, CancellationToken cancellationToken = default)
    {
        var importJob = new BackgroundJob(
            Guid.NewGuid(),
            "Apple Music XML Import",
            async token =>
            {
                token.ThrowIfCancellationRequested();
                var module = new AppleMusicLibrary();
                module.Load(xmlPath);
                module.ParseTracks();
                module.AddToDatabase(_dbPath);
                await Task.CompletedTask;
            });

        return EnqueueWithNormalizeAndMatchAsync(importJob, cancellationToken);
    }

    public ValueTask QueueMediaScanAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var scanJob = new BackgroundJob(
            Guid.NewGuid(),
            "Media Library Scan",
            async token =>
            {
                token.ThrowIfCancellationRequested();
                var scanner = new FileLibraryScanner();
                scanner.ScanAndImport(rootPath, _dbPath);
                await Task.CompletedTask;
            });

        return EnqueueWithNormalizeAndMatchAsync(scanJob, cancellationToken);
    }

    public ValueTask QueueNormalizeAndMatchAsync(CancellationToken cancellationToken = default)
    {
        var normalizeJob = BuildNormalizeAndMatchJob();
        return _queue.EnqueueAsync(normalizeJob, cancellationToken);
    }

    public ValueTask QueueRebuildIndexAsync(CancellationToken cancellationToken = default)
    {
        var job = BuildRebuildIndexJob();
        return _queue.EnqueueAsync(job, cancellationToken);
    }

    public ValueTask QueueCleanupAsync(double minConfidence, CancellationToken cancellationToken = default)
    {
        var job = new BackgroundJob(
            Guid.NewGuid(),
            "Cleanup",
            async token =>
            {
                token.ThrowIfCancellationRequested();
                DatabaseInitializer.Initialize(_dbPath);
                var confidence = Math.Clamp(minConfidence, 0.0, 1.0);
                LibraryCleaner.Run(confidence, dryRun: false, dbPath: _dbPath);
                TrackLibraryIndexBuilder.Rebuild(_dbPath);
                await Task.CompletedTask;
            });

        return _queue.EnqueueAsync(job, cancellationToken);
    }

    public ValueTask QueueMatchRescanAsync(double minScore, CancellationToken cancellationToken = default)
    {
        var job = new BackgroundJob(
            Guid.NewGuid(),
            "Match Rescan",
            async token =>
            {
                token.ThrowIfCancellationRequested();
                DatabaseInitializer.Initialize(_dbPath);
                var score = Math.Clamp(minScore, 0.0, 1.0);
                MatchEngine.Run(dryRun: false, dbPath: _dbPath, minAutoScore: score);
                TrackLibraryIndexBuilder.Rebuild(_dbPath);
                await Task.CompletedTask;
            });

        return _queue.EnqueueAsync(job, cancellationToken);
    }

    /// <summary>Enqueues <paramref name="firstJob"/> and then immediately queues a Normalize+Match job behind it.</summary>
    private async ValueTask EnqueueWithNormalizeAndMatchAsync(BackgroundJob firstJob, CancellationToken cancellationToken)
    {
        await _queue.EnqueueAsync(firstJob, cancellationToken);
        await _queue.EnqueueAsync(BuildNormalizeAndMatchJob(), cancellationToken);
    }

    private BackgroundJob BuildNormalizeAndMatchJob()
    {
        return new BackgroundJob(
            Guid.NewGuid(),
            "Normalize + Match",
            async token =>
            {
                token.ThrowIfCancellationRequested();
                DatabaseInitializer.Initialize(_dbPath);
                LibraryCleaner.Run(minConfidence: 0.45, dryRun: false, dbPath: _dbPath);
                MatchEngine.Run(dryRun: false, dbPath: _dbPath, minAutoScore: 0.92);
                TrackLibraryIndexBuilder.Rebuild(_dbPath);
                await Task.CompletedTask;
            });
    }

    private BackgroundJob BuildRebuildIndexJob()
    {
        return new BackgroundJob(
            Guid.NewGuid(),
            "Rebuild Artist/Album Index",
            async token =>
            {
                token.ThrowIfCancellationRequested();
                TrackLibraryIndexBuilder.Rebuild(_dbPath);
                await Task.CompletedTask;
            });
    }
}
