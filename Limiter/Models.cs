using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Limiter;

public sealed class AppUsage
{
    public int ProcessId { get; set; }
    public List<AppUsage> Processes { get; set; } = [];
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long DownloadBytes { get; set; }
    public long UploadBytes { get; set; }
    public long LastDownloadBytes { get; set; }
    public long LastUploadBytes { get; set; }
    public double DownloadRate { get; set; }
    public double UploadRate { get; set; }
    public bool HasRateSample { get; set; }
    public int DownloadLimit { get; set; }
    public int UploadLimit { get; set; }
    public bool DownloadLimitEnabled { get; set; } = true;
    public bool UploadLimitEnabled { get; set; } = true;
    public bool BlockDownload { get; set; }
    public bool BlockUpload { get; set; }
    public AppRule? ParentRule { get; set; }
    public int AppDownloadLimit { get; set; }
    public int AppUploadLimit { get; set; }
}

public sealed class AppRule
{
    public string Path { get; set; } = "";
    public int DownloadKBps { get; set; }
    public int UploadKBps { get; set; }
    public bool DownloadLimitEnabled { get; set; } = true;
    public bool UploadLimitEnabled { get; set; } = true;
    public bool BlockDownload { get; set; }
    public bool BlockUpload { get; set; }
}

public sealed class RuleStore
{
    private readonly string _file;
    private readonly Dictionary<string, AppRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    // A failed load must never turn into an empty file on the next save.
    internal string? LoadError { get; private set; }

    public RuleStore() : this(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limiter", "rules.json")) { }

    internal RuleStore(string file)
    {
        _file = file;
        try
        {
            if (!File.Exists(_file)) return;
            var loaded = JsonSerializer.Deserialize<List<AppRule>>(File.ReadAllText(_file))
                ?? throw new InvalidDataException("The rule file is empty or null.");
            var valid = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in loaded)
            {
                if (rule is null || string.IsNullOrWhiteSpace(rule.Path))
                    throw new InvalidDataException("A rule entry has no application path.");
                valid[rule.Path] = rule;
            }
            foreach (var pair in valid) _rules[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            // Keep the file untouched so the user can repair or remove it.
            _rules.Clear();
            LoadError = _file + ": " + ex.Message;
        }
    }

    public AppRule Get(string path) => _rules.TryGetValue(path, out var rule) ? rule : new AppRule { Path = path };

    public int GetLimit(string path, bool outbound)
        => _rules.TryGetValue(path, out var rule) ? (outbound
            ? (rule.UploadLimitEnabled ? rule.UploadKBps : 0)
            : (rule.DownloadLimitEnabled ? rule.DownloadKBps : 0)) : 0;

    internal bool IsBlocked(string path, bool outbound)
        => _rules.TryGetValue(path, out var rule) && (outbound ? rule.BlockUpload : rule.BlockDownload);

    public void Set(AppRule rule)
    {
        if (LoadError is not null)
            throw new InvalidOperationException(Localization.T("rules-could-not-be-saved-until-the-file-is-fixed"));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        var updated = new Dictionary<string, AppRule>(_rules, StringComparer.OrdinalIgnoreCase) { [rule.Path] = rule };
        string temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated.Values.OrderBy(x => x.Path), new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(_file)) File.Replace(temporary, _file, null);
        else File.Move(temporary, _file);
        _rules[rule.Path] = rule;
    }
}
