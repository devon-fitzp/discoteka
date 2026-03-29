using Discoteka.Core.Models;
using Microsoft.Data.Sqlite;

namespace Discoteka.Core.Database;

/// <summary>
/// Seeds the default dynamic playlists ("Most Played", "Deeper Cuts") on first run.
/// </summary>
public static class PlaylistSeedService
{
    public static async Task EnsureDefaultPlaylistsAsync(IDynamicPlaylistRepository repository, string? dbPath = null, CancellationToken cancellationToken = default)
    {
        var path = dbPath ?? DbPaths.GetDefaultDbPath();
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(path));
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM DynamicPlaylists;";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            return;
        }

        await repository.InsertAsync(new DynamicPlaylist
        {
            Name = "Most Played",
            RuleField = "Plays",
            Operator = ">=",
            ValueA = 10
        }, cancellationToken);

        await repository.InsertAsync(new DynamicPlaylist
        {
            Name = "Deeper Cuts",
            RuleField = "Plays",
            Operator = "between",
            ValueA = 2,
            ValueB = 10
        }, cancellationToken);

        Console.WriteLine("[Playlists] Seeded default dynamic playlists.");
    }
}
