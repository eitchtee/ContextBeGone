using System.IO;
using System.Text;

namespace ContextBeGone.Services;

/// <summary>
/// The small amount of state the app remembers between runs, kept in the portable data folder.
///
/// Right now that is only the list of file types you asked to see. Nothing here touches the
/// registry: adding a type just loads more scopes into the list to look at.
/// </summary>
public static class Settings
{
    private static string FileTypesPath => Path.Combine(BackupService.BackupRoot, "file-types.txt");

    /// <summary>File extensions the user added, in the order they added them.</summary>
    public static List<string> FileTypes()
    {
        try
        {
            if (!File.Exists(FileTypesPath)) return new List<string>();

            return File.ReadAllLines(FileTypesPath)
                       .Select(line => line.Trim())
                       .Where(line => line.Length > 0 && !line.StartsWith('#'))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    public static void SaveFileTypes(IEnumerable<string> extensions)
    {
        try
        {
            Directory.CreateDirectory(BackupService.BackupRoot);

            var body = new StringBuilder();
            body.AppendLine("# File types added in ContextBeGone. Purely a view setting —");
            body.AppendLine("# nothing here changes the registry. Delete a line to stop listing it.");
            foreach (var ext in extensions.Distinct(StringComparer.OrdinalIgnoreCase))
                body.AppendLine(ext);

            File.WriteAllText(FileTypesPath, body.ToString(), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Losing the list only means the types must be re-added next run.
        }
    }

    /// <summary>Normalises user input to a leading-dot extension, or null when it is not usable.</summary>
    public static string? NormaliseExtension(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        // Accept "png", ".png" and even a whole file name like "photo.png".
        var lastDot = text.LastIndexOf('.');
        if (lastDot > 0) text = text[lastDot..];
        if (!text.StartsWith('.')) text = "." + text;

        return text.Length > 1 && text.Skip(1).All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? text.ToLowerInvariant()
            : null;
    }
}
