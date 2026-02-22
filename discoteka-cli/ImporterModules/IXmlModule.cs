namespace discoteka_cli.ImporterModules;

public interface IXmlModule
{
    void Load(string filePath);
    int ParseTracks();
    int AddToDatabase(string? dbPath = null);
}
