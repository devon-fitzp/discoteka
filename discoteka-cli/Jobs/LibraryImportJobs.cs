using discoteka_cli.Database;
using discoteka_cli.ImporterModules;
using discoteka_cli.Utils;

namespace discoteka_cli.Jobs;

public interface ILibraryImportJobs
{
    ValueTask QueueAppleMusicImportAsync(string xmlPath, CancellationToken cancellationToken = default);
    ValueTask QueueMediaScanAsync(string rootPath, CancellationToken cancellationToken = default);
    ValueTask QueueCleanupAsync(double minConfidence, CancellationToken cancellationToken = default);
    ValueTask QueueMatchRescanAsync(double minScore, CancellationToken cancellationToken = default);
    ValueTask QueueNormalizeAndMatchAsync(CancellationToken cancellationToken = default);
    ValueTask QueueRebuildIndexAsync(CancellationToken cancellationToken = default);
}

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
