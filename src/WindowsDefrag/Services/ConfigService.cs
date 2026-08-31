using System.IO;
using System.Text.Json;
using WindowsDefrag.Models;

namespace WindowsDefrag.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _configPath;
    private AppConfig _config = new();

    public ConfigService(string? configPath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppInfo.CompanyFolderName,
            AppInfo.AppDataFolderName);

        Directory.CreateDirectory(appData);
        _configPath = configPath ?? Path.Combine(appData, "config.json");
        Load();
    }

    public AppConfig Config => _config;
    public string ConfigPath => _configPath;

    public string AppDataDirectory =>
        Path.GetDirectoryName(_configPath) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppInfo.CompanyFolderName, AppInfo.AppDataFolderName);

    public string ReportsDirectory
    {
        get
        {
            var dir = Path.Combine(AppDataDirectory, "reports");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                return;
            }

            var defaultPath = Path.Combine(AppContext.BaseDirectory, "Assets", "config.default.json");
            if (File.Exists(defaultPath))
            {
                var json = File.ReadAllText(defaultPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            else
            {
                _config = new AppConfig();
            }

            Save();
        }
        catch
        {
            _config = new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions));
        }
        catch
        {
            // ignore
        }
    }
}
