using Discoteka.Core.Database;
using Discoteka.Core.Models;

namespace Discoteka.Core.Utils;

/// <summary>
/// Reads, writes, and manages M3U8 playlist files stored in the Playlists folder
/// alongside the Discoteka database.
/// </summary>
public sealed class M3uPlaylistService
{
    private readonly string _folder;

    public M3uPlaylistService(string? dbPath = null)
    {
        var dir = Path.GetDirectoryName(dbPath ?? DbPaths.GetDefaultDbPath())
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _folder = Path.Combine(dir, "Playlists");
    }

    public string PlaylistsFolder
    {
        get
        {
            Directory.CreateDirectory(_folder);
            return _folder;
        }
    }

    /// <summary>Returns all static playlists found in the Playlists folder.</summary>
    public IReadOnlyList<StaticPlaylist> GetAll()
    {
        Directory.CreateDirectory(_folder);
        return Directory.EnumerateFiles(_folder, "*.m3u8")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new StaticPlaylist(Path.GetFileNameWithoutExtension(f), f))
            .ToList();
    }

    /// <summary>
    /// Returns the absolute file paths listed in the playlist, skipping comment lines.
    /// </summary>
    public IReadOnlyList<string> LoadPaths(StaticPlaylist playlist)
    {
        if (!File.Exists(playlist.FilePath))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(playlist.FilePath)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .ToList();
    }

    /// <summary>Writes (or overwrites) a named .m3u8 file with the given absolute paths.</summary>
    public StaticPlaylist Save(string name, IEnumerable<string> filePaths)
    {
        Directory.CreateDirectory(_folder);
        var safeName = MakeSafeFileName(name);
        var path = Path.Combine(_folder, safeName + ".m3u8");
        var lines = new List<string> { "#EXTM3U" };
        lines.AddRange(filePaths);
        File.WriteAllLines(path, lines);
        return new StaticPlaylist(name, path);
    }

    public void Delete(StaticPlaylist playlist)
    {
        if (File.Exists(playlist.FilePath))
        {
            File.Delete(playlist.FilePath);
        }
    }

    public StaticPlaylist Rename(StaticPlaylist playlist, string newName)
    {
        Directory.CreateDirectory(_folder);
        var safeName = MakeSafeFileName(newName);
        var newPath = Path.Combine(_folder, safeName + ".m3u8");
        if (File.Exists(playlist.FilePath))
        {
            File.Move(playlist.FilePath, newPath);
        }

        playlist.Name = newName;
        playlist.FilePath = newPath;
        return playlist;
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
