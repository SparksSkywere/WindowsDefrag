using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WindowsDefrag.Helpers;
using WindowsDefrag.Services;
using WindowsDefrag.Themes;

namespace WindowsDefrag.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private bool _loading;

    [ObservableProperty] private bool _confirmBeforeDefrag;
    [ObservableProperty] private bool _analyseOnStartup;
    [ObservableProperty] private bool _showRemovable;
    [ObservableProperty] private bool _shadedView;
    [ObservableProperty] private double _blockSize = 8;
    [ObservableProperty] private bool _blockSsd;
    [ObservableProperty] private bool _skipBattery;
    [ObservableProperty] private bool _skipPagefile;
    [ObservableProperty] private bool _skipHibernation;
    [ObservableProperty] private double _minFree = 8;
    [ObservableProperty] private string _hourText = "2";
    [ObservableProperty] private string _minuteText = "00";
    [ObservableProperty] private ThemeOption? _selectedTheme;

    public ObservableCollection<ThemeOption> ThemeOptions { get; } = new(ThemeCatalog.PickerOptions);

    public SettingsViewModel(ConfigService config)
    {
        _config = config;
        LoadFromConfig();
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (_loading || value is null)
            return;
        ThemeManager.Apply(value.Id);
    }

    public void RevertThemePreview() =>
        ThemeManager.Apply(_config.Config.General.Theme);

    public void LoadFromConfig()
    {
        _loading = true;
        var g = _config.Config.General;
        var p = _config.Config.Protection;
        var s = _config.Config.Schedule;

        ConfirmBeforeDefrag = g.ConfirmBeforeDefrag;
        AnalyseOnStartup = g.AnalyzeOnStartup;
        ShowRemovable = g.ShowRemovable;
        ShadedView = g.ShadedClusterView;
        BlockSize = Math.Clamp(g.ClusterBlockSize, 4, 16);
        BlockSsd = p.BlockSsdDefrag;
        SkipBattery = p.SkipOnBattery;
        SkipPagefile = p.SkipPagefile;
        SkipHibernation = p.SkipHibernation;
        MinFree = Math.Clamp(p.MinFreePercent, 2, 25);
        HourText = s.Hour.ToString();
        MinuteText = s.Minute.ToString("00");

        var id = ThemeCatalog.NormalizeId(g.Theme);
        SelectedTheme = ThemeOptions.FirstOrDefault(t => t.Id == id) ?? ThemeOptions[0];
        _loading = false;
    }

    public void Save()
    {
        var g = _config.Config.General;
        var p = _config.Config.Protection;
        var s = _config.Config.Schedule;

        g.ConfirmBeforeDefrag = ConfirmBeforeDefrag;
        g.AnalyzeOnStartup = AnalyseOnStartup;
        g.ShowRemovable = ShowRemovable;
        g.ShadedClusterView = ShadedView;
        g.ClusterBlockSize = (int)BlockSize;
        var themeId = SelectedTheme?.Id ?? ThemeCatalog.SystemId;
        g.Theme = ThemeManager.ToConfigValue(themeId);
        ThemeManager.Apply(themeId);

        p.BlockSsdDefrag = BlockSsd;
        p.SkipOnBattery = SkipBattery;
        p.SkipPagefile = SkipPagefile;
        p.SkipHibernation = SkipHibernation;
        p.MinFreePercent = (int)MinFree;

        if (int.TryParse(HourText, out var h))
            s.Hour = Math.Clamp(h, 0, 23);
        if (int.TryParse(MinuteText, out var m))
            s.Minute = Math.Clamp(m, 0, 59);

        _config.Save();
    }
}
