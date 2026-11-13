using System.Text.Json;
using System.Text.Json.Serialization;

namespace TartarusStudio.Core;

public static class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig Load(string path)
    {
        AppConfig? cfg = null;
        if (File.Exists(path))
        {
            try { cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json); }
            catch (JsonException)
            {
                // Keep the unreadable file for the user instead of silently overwriting it.
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
        }
        cfg ??= new AppConfig();
        cfg.Normalize();
        return cfg;
    }

    public static void Save(string path, AppConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static string ExportProfile(Profile p) => JsonSerializer.Serialize(p, Json);

    public static Profile ImportProfile(string json)
    {
        var p = JsonSerializer.Deserialize<Profile>(json, Json) ?? throw new JsonException("Empty profile");
        p.Id = Guid.NewGuid();
        if (p.Keymaps.Count == 0) p.Keymaps.Add(new Keymap { Name = "Keymap 1", LedGreen = true });
        return p;
    }
}

public static class ProfileResolver
{
    /// <summary>Finds the profile linked to a foreground executable; full-path links win over bare names.</summary>
    public static Profile Resolve(AppConfig cfg, string? exePath)
    {
        if (!string.IsNullOrEmpty(exePath))
        {
            var name = Path.GetFileName(exePath.Replace('\\', '/'));
            Profile? byName = null;
            foreach (var p in cfg.Profiles)
                foreach (var link in p.LinkedPrograms)
                {
                    if (string.Equals(link, exePath, StringComparison.OrdinalIgnoreCase)) return p;
                    if (byName == null && string.Equals(Path.GetFileName(link.Replace('\\', '/')), name, StringComparison.OrdinalIgnoreCase)) byName = p;
                }
            if (byName != null) return byName;
        }
        return cfg.DefaultProfile;
    }
}
