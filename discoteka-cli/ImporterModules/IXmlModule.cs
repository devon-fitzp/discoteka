namespace discoteka_cli.ImporterModules;

/// <summary>
/// Common interface for XML-based library importers (Apple Music/iTunes and Rekordbox).
/// Implementations follow a three-step pipeline:
/// <list type="number">
///   <item><term><see cref="Load"/></term><description>Parse the file into memory.</description></item>
///   <item><term><see cref="ParseTracks"/></term><description>Extract track objects from the parsed document.</description></item>
///   <item><term><see cref="AddToDatabase"/></term><description>Upsert the tracks into the SQLite store, skipping duplicates.</description></item>
/// </list>
/// </summary>
public interface IXmlModule
{
    /// <summary>Loads and parses the XML file at <paramref name="filePath"/> into memory.</summary>
    /// <exception cref="System.IO.FileNotFoundException">Thrown if the file does not exist.</exception>
    void Load(string filePath);

    /// <summary>
    /// Walks the in-memory XML document and populates the internal track list.
    /// Must be called after <see cref="Load"/>.
    /// </summary>
    /// <returns>The number of tracks parsed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="Load"/> has not been called.</exception>
    int ParseTracks();

    /// <summary>
    /// Inserts the parsed tracks into the SQLite database, skipping any that already exist.
    /// Must be called after <see cref="ParseTracks"/>.
    /// </summary>
    /// <param name="dbPath">
    /// Optional path to the database file. Defaults to <see cref="discoteka_cli.Database.DbPaths.GetDefaultDbPath"/>.
    /// </param>
    /// <returns>The number of new rows inserted.</returns>
    int AddToDatabase(string? dbPath = null);
}
