using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.Json;

namespace HomeRobot.Server.Dashboard;

internal sealed class DashboardSettings
{
    private static readonly Dictionary<string, Func<string, bool>> Fields = new()
    {
        ["Ollama:BaseUrl"] = SafeUrl,
        ["Ollama:Model"] = value => Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,127}\z"),
        ["Ollama:TimeoutSeconds"] = value => Integer(value, 1, 600),
        ["Ollama:Temperature"] = value => double.TryParse(value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 2,
        ["Ollama:MaxResponseTokens"] = value => Integer(value, 1, 8192),
        ["Conversation:MaxHistoryMessages"] = value => Integer(value, 0, 40) &&
            int.Parse(value, CultureInfo.InvariantCulture) % 2 == 0,
        ["Speech:STT:Language"] = value => Regex.IsMatch(value, @"\A(?:[a-z]{2,3})?\z"),
        ["Speech:STT:ModelPath"] = value => ModelPath(value, "models/stt/"),
        ["Speech:STT:TimeoutSeconds"] = value => Integer(value, 1, 600),
        ["Speech:TTS:VoiceModelPath"] = value => ModelPath(value, "models/tts/") && value.EndsWith(".onnx", StringComparison.Ordinal),
        ["Speech:TTS:TimeoutSeconds"] = value => Integer(value, 1, 600),
        ["Vision:ContextLifetimeSeconds"] = value => Integer(value, 1, 600)
    };

    private readonly string path;
    private readonly object gate = new();
    private Dictionary<string, string?> saved;
    public IReadOnlyDictionary<string, string?> Startup { get; }
    internal sealed class SettingsProvider : ConfigurationProvider
    {
        public SettingsProvider(IReadOnlyDictionary<string, string?> values) =>
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SettingsSource(IReadOnlyDictionary<string, string?> values) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new SettingsProvider(values);
    }

    public DashboardSettings(string path)
    {
        this.path = Path.GetFullPath(path);
        using var stream = File.Exists(this.path) ? File.OpenRead(this.path) : null;
        saved = stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string?>>(stream)
            ?? throw new InvalidDataException("Invalid dashboard settings file.");
        if (Validate(saved).Length > 0)
            throw new InvalidDataException("Invalid dashboard settings file.");
        Startup = saved;
    }

    public void AddStartupOverrides(ConfigurationManager configuration)
    {
        var index = configuration.Sources.ToList().FindLastIndex(source =>
            source is JsonConfigurationSource json && json.Path is not null &&
            Path.GetFileName(json.Path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
        configuration.Sources.Insert(index + 1, new SettingsSource(Startup));
    }

    public Dictionary<string, string?> Snapshot()
    {
        lock (gate)
            return new(saved);
    }

    public void Save(Dictionary<string, string?> values, bool merge)
    {
        lock (gate)
        {
            var updated = merge ? new Dictionary<string, string?>(saved) : [];
            foreach (var (key, value) in values)
            {
                if (value is null)
                    updated.Remove(key);
                else
                    updated[key] = value;
            }

            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew))
                {
                    JsonSerializer.Serialize(stream, updated);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                saved = updated;
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    public static string[] Validate(Dictionary<string, string?> values, bool allowRemoval = false) =>
        values.Where(entry => !Fields.TryGetValue(entry.Key, out var valid) ||
            !(entry.Value is null ? allowRemoval : entry.Value.Length <= 512 && valid(entry.Value)))
            .Select(entry => Fields.ContainsKey(entry.Key) ? entry.Key : "Unknown setting").Distinct().ToArray();

    public static bool SafeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" &&
        url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0 &&
        url.AbsolutePath == "/" && !value.Any(char.IsWhiteSpace);

    private static bool Integer(string value, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
        number >= minimum && number <= maximum;

    private static bool ModelPath(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value[prefix.Length..].Split('/').All(part => part is not ("." or "..") &&
            Regex.IsMatch(part, @"\A[a-zA-Z0-9_.-]+\z"));
}
