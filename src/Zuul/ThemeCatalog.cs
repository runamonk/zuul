using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Zuul.Core;

namespace Zuul;

// Same themes.json format as Brain Pending, so a palette can be copied between the two.
internal sealed class ThemeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string FilePath { get; }
    public IReadOnlyList<AppColorTheme> Themes { get; private set; } = AppThemes.All;

    public ThemeCatalog(string? settingsPath = null)
    {
        var directory = settingsPath == null ? ZuulSettings.Folder : Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        FilePath = Path.Combine(directory, "themes.json");
    }

    public AppColorTheme Resolve(ZuulSettings settings, bool? systemDark = null)
    {
        var fallback = AppThemes.Resolve(settings, systemDark);
        return Themes.FirstOrDefault(t => string.Equals(t.Name, settings.ColorTheme, StringComparison.OrdinalIgnoreCase))
            ?? Themes.FirstOrDefault(t => t.Name == fallback.Name)
            ?? fallback;
    }

    public void EnsureFile()
    {
        if (!File.Exists(FilePath)) AtomicFile.WriteAllText(FilePath, Serialize(AppThemes.All));
    }

    // Only publish a fully validated catalog; a broken file keeps the current palette.
    public void Reload()
    {
        EnsureFile();
        Themes = Parse(File.ReadAllText(FilePath));
    }

    internal static AppColorTheme[] Parse(string json)
    {
        var themes = JsonSerializer.Deserialize<AppColorTheme[]>(json, JsonOptions);
        if (themes == null || themes.Length == 0) throw new InvalidDataException("Themes must be a non-empty JSON array.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes)
        {
            if (theme == null || string.IsNullOrWhiteSpace(theme.Name) || theme.Name != theme.Name.Trim())
                throw new InvalidDataException("Every theme needs a name without leading or trailing spaces.");
            if (!names.Add(theme.Name)) throw new InvalidDataException($"Duplicate theme name: {theme.Name}.");
            foreach (var property in typeof(AppColorTheme).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name != "Name"))
            {
                var value = (string?)property.GetValue(theme);
                if (value == null && property.Name is nameof(AppColorTheme.Swatch) or nameof(AppColorTheme.Code) or nameof(AppColorTheme.Tooltip)
                    or nameof(AppColorTheme.AccentText) or nameof(AppColorTheme.Notice) or nameof(AppColorTheme.NoticeText) or nameof(AppColorTheme.Error)) continue; // Optional.
                if (value == null || !Color.TryParse(value, out _))
                    throw new InvalidDataException($"Theme '{theme.Name}' has an invalid or missing {property.Name} color.");
            }
        }
        return themes;
    }

    internal static string Serialize(IEnumerable<AppColorTheme> themes) =>
        JsonSerializer.Serialize(themes, JsonOptions) + Environment.NewLine;
}
