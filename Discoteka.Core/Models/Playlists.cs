namespace Discoteka.Core.Models;

public sealed class StaticPlaylist
{
    public StaticPlaylist(string name, string filePath)
    {
        Name = name;
        FilePath = filePath;
    }

    public string Name { get; set; }
    public string FilePath { get; set; }
}

public sealed class DynamicPlaylist
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Column to filter on. Currently always "Plays".</summary>
    public string RuleField { get; set; } = "Plays";

    /// <summary>Comparison operator: ">=", "<=", or "between".</summary>
    public string Operator { get; set; } = ">=";

    public int ValueA { get; set; }

    /// <summary>Upper bound — only used when Operator is "between".</summary>
    public int? ValueB { get; set; }
}
