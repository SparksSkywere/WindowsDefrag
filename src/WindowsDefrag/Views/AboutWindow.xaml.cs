using System.Windows;
using WindowsDefrag.Helpers;

namespace WindowsDefrag.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);
        SourceInitialized += (_, _) =>
            WindowChromeHelper.ApplyTheme(this, ThemeManager.IsDarkEffective);
        ThemeManager.RefreshWindow(this);

        Title = $"About {AppInfo.ProductName}";
        TitleText.Text = AppInfo.ProductName;
        VersionText.Text = $"Version {AppInfo.Version}";
        DescriptionText.Text = AppInfo.Description;
        CompanyText.Text = AppInfo.Company;
        CopyrightText.Text = AppInfo.Copyright;

        var icon = AppIcon.GetImageSource();
        if (icon is not null)
            AppIconImage.Source = icon;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
