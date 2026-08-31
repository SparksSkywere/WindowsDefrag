using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowsDefrag.Controls;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;
using WindowsDefrag.Services;
using WindowsDefrag.ViewModels;
using WindowsDefrag.Views;

namespace WindowsDefrag;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ConfigService _config;

    public MainWindow(MainViewModel viewModel, ConfigService config)
    {
        InitializeComponent();
        Title = AppInfo.ProductName;
        AppIcon.ApplyTo(this);
        DataContext = viewModel;
        _viewModel = viewModel;
        _config = config;

        ClusterMap.BlockHovered += OnBlockHovered;
        ClusterMap.BlockActivated += OnBlockActivated;

        SourceInitialized += (_, _) =>
            WindowChromeHelper.ApplyTheme(this, ThemeManager.IsDarkEffective);

        Loaded += (_, _) =>
        {
            ThemeManager.RefreshWindow(this);
            if (viewModel.LoadedCommand.CanExecute(null))
                viewModel.LoadedCommand.Execute(null);
        };
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var vm = new SettingsViewModel(_config);
        var options = new SettingsWindow(vm) { Owner = this };
        if (options.ShowDialog() == true)
        {
            _viewModel.ShadedView = _config.Config.General.ShadedClusterView;
            _viewModel.ClusterBlockSize = _config.Config.General.ClusterBlockSize;
        }
    }

    private void SettingsChanged_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SaveGeneralSettings();

    private void TabCluster_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 0;
    private void TabFiles_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 1;
    private void TabDrive_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 2;
    private void TabReports_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 3;
    private void TabJobs_Click(object sender, RoutedEventArgs e) => BottomTabs.SelectedIndex = 4;

    private void BottomTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SelectedTab = BottomTabs.SelectedIndex switch
        {
            1 => BottomTab.FileStatus,
            2 => BottomTab.DriveStatus,
            3 => BottomTab.Reports,
            4 => BottomTab.Jobs,
            _ => BottomTab.ClusterView
        };
    }

    private void DriveGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.AnalyseCommand.CanExecute(null))
            _viewModel.AnalyseCommand.Execute(null);
    }

    private void ReportList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenReportCommand.CanExecute(null))
            _viewModel.OpenReportCommand.Execute(null);
    }

    private void OnBlockHovered(int index, long start, long count, ClusterKind kind)
    {
        _viewModel.LegendHint =
            $"Block {index:N0}  ·  LCN {start:N0}–{start + count - 1:N0}  ·  {kind}";
    }

    private void OnBlockActivated(int index, long start, long count)
    {
        var files = _viewModel.InspectBlock(index);
        var win = new ClusterInspectorWindow(index, start, count, files) { Owner = this };
        win.ShowDialog();
    }
}
