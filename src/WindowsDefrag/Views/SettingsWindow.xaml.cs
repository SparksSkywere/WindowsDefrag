using System.Windows;
using WindowsDefrag.Helpers;
using WindowsDefrag.ViewModels;

namespace WindowsDefrag.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) =>
            WindowChromeHelper.ApplyTheme(this, ThemeManager.IsDarkEffective);
        ThemeManager.RefreshWindow(this);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RevertThemePreview();
        DialogResult = false;
        Close();
    }
}
