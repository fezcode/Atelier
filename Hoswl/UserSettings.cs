using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelier.Hoswl
{
    /// <summary>
    /// Per-user settings, kept beside the wallpaper cache in
    /// <c>%APPDATA%\fezcode\Atelier\settings.json</c>. Tiny on purpose — Atelier had
    /// no settings file before the Hisashi integration needed two switches.
    /// </summary>
    public sealed class UserSettings
    {
        [JsonPropertyName("hisashiIntegration")] public bool HisashiIntegration { get; set; } = false;
        [JsonPropertyName("hisashiMenus")]       public bool HisashiMenus       { get; set; } = true;

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "fezcode", "Atelier", "settings.json");

        /// <summary>Where <see cref="Save"/> writes; null means <see cref="DefaultPath"/>. Tests point it at a temp file.</summary>
        [JsonIgnore] public string? FilePath { get; set; }

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static UserSettings Load(string? path = null)
        {
            path ??= DefaultPath;
            UserSettings s = new();
            try
            {
                if (File.Exists(path))
                    s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Options) ?? new UserSettings();
            }
            catch (Exception)
            {
                // A corrupt file is treated as defaults; the next Save rewrites it.
            }
            s.FilePath = path;
            return s;
        }

        public void Save(string? path = null)
        {
            path ??= FilePath ?? DefaultPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            }
            catch (Exception)
            {
                // Never let a settings write take the app down.
            }
        }
    }
}
