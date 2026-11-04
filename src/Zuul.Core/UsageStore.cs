using System.Text.Json;

namespace Zuul.Core;

// Counts launches per result, so the things you use float to the top.
public sealed class UsageStore
{
    private readonly string? _filePath;
    private Dictionary<string, int> _counts;

    public UsageStore(string? filePath)
    {
        _filePath = filePath;
        _counts = Read(filePath);
    }

    public int Count(string key) => _counts.GetValueOrDefault(key);

    // Capped, so one app used every hour for a year can't bury an exact match.
    public int Boost(string key) => Math.Min(Count(key), 20) * 5;

    public void Record(string key)
    {
        _counts[key] = Count(key) + 1;
        if (_filePath == null) return;
        try { AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(_counts)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, int> Read(string? path)
    {
        if (path == null || !File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
            return new(counts ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(StringComparer.OrdinalIgnoreCase); }
    }
}
