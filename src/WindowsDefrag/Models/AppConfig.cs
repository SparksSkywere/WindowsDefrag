using System.Text.Json.Serialization;

namespace WindowsDefrag.Models;

public sealed class AppConfig
{
    [JsonPropertyName("general")]
    public GeneralSettings General { get; set; } = new();

    [JsonPropertyName("protection")]
    public ProtectionSettings Protection { get; set; } = new();

    [JsonPropertyName("schedule")]
    public ScheduleSettings Schedule { get; set; } = new();
}

public sealed class GeneralSettings
{
    public bool ConfirmBeforeDefrag { get; set; } = true;
    public bool AnalyzeOnStartup { get; set; } = true;
    public string DefaultMethod { get; set; } = "Space";
    public int ClusterBlockSize { get; set; } = 8;
    public bool ShadedClusterView { get; set; } = true;
    public bool ShowRemovable { get; set; }
    public string Theme { get; set; } = "system";
}

public sealed class ProtectionSettings
{
    public bool BlockSsdDefrag { get; set; } = true;
    public bool SkipOnBattery { get; set; } = true;
    public int MinFreePercent { get; set; } = 8;
    public bool SkipPagefile { get; set; } = true;
    public bool SkipHibernation { get; set; } = true;
    public List<string> ExcludePaths { get; set; } = [];
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public int Hour { get; set; } = 2;
    public int Minute { get; set; }
    public string Method { get; set; } = "Space";
    public string DriveLetters { get; set; } = "";
}
