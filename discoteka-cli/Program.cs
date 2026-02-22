namespace discoteka_cli;

using System.Xml.Linq;
using discoteka_cli.ImporterModules;
using discoteka_cli.Utils;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  discoteka-cli xml <path-to-xml>");
            Console.WriteLine("  discoteka-cli scan <path-to-music-folder>");
            Console.WriteLine("  discoteka-cli clean [--confidence <0-100>] [--dry-run]");
            Console.WriteLine("  discoteka-cli match [--dry-run]");
            return;
        }

        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "xml":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: discoteka-cli xml <path-to-xml>");
                    return;
                }
                RunXmlImport(args[1]);
                break;
            case "scan":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: discoteka-cli scan <path-to-music-folder>");
                    return;
                }
                RunFileScan(args[1]);
                break;
            case "clean":
                RunClean(args);
                break;
            case "match":
                RunMatch(args);
                break;
            default:
                Console.WriteLine($"Unknown command: {command}");
                Console.WriteLine("Expected \"xml\", \"scan\", \"clean\", or \"match\".");
                break;
        }
    }

    private static void RunXmlImport(string xmlPath)
    {
        if (!File.Exists(xmlPath))
        {
            Console.WriteLine($"File not found: {xmlPath}");
            return;
        }

        var importer = ResolveImporter(xmlPath);
        if (importer == null)
        {
            Console.WriteLine("Unsupported XML format. Expected Apple Music or Rekordbox library XML.");
            return;
        }

        importer.Load(xmlPath);
        var parsed = importer.ParseTracks();
        var inserted = importer.AddToDatabase();

        Console.WriteLine($"Parsed {parsed} tracks.");
        Console.WriteLine($"Inserted {inserted} new tracks into the database.");
    }

    private static void RunFileScan(string rootPath)
    {
        try
        {
            var scanner = new FileLibraryScanner();
            var inserted = scanner.ScanAndImport(rootPath);
            Console.WriteLine($"Inserted {inserted} new files into the database.");
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    private static void RunClean(string[] args)
    {
        var minConfidence = 0.7;
        var dryRun = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (arg.Equals("--confidence", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var value = args[i + 1];
                i++;
                if (double.TryParse(value, out var parsed))
                {
                    minConfidence = parsed > 1 ? parsed / 100.0 : parsed;
                }
            }
        }

        minConfidence = Math.Clamp(minConfidence, 0.0, 1.0);
        LibraryCleaner.Run(minConfidence, dryRun);
    }

    private static void RunMatch(string[] args)
    {
        var dryRun = args.Any(arg => arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        MatchEngine.Run(dryRun);
    }

    private static IXmlModule? ResolveImporter(string xmlPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(xmlPath);
        }
        catch
        {
            return null;
        }

        var rootName = doc.Root?.Name.LocalName;
        if (string.Equals(rootName, "plist", StringComparison.OrdinalIgnoreCase))
        {
            return new AppleMusicLibrary();
        }

        if (string.Equals(rootName, "DJ_PLAYLISTS", StringComparison.OrdinalIgnoreCase))
        {
            var productName = doc.Root?.Element("PRODUCT")?.Attribute("Name")?.Value;
            if (string.Equals(productName, "rekordbox", StringComparison.OrdinalIgnoreCase))
            {
                return new RekordboxLibrary();
            }
        }

        return null;
    }
}
