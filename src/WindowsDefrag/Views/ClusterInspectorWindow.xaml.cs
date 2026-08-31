using System.Windows;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;

namespace WindowsDefrag.Views;

public partial class ClusterInspectorWindow : Window
{
    public ClusterInspectorWindow(int block, long startLcn, long count, IReadOnlyList<FileRecord> files)
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);
        SourceInitialized += (_, _) =>
            WindowChromeHelper.ApplyTheme(this, ThemeManager.IsDarkEffective);
        ThemeManager.RefreshWindow(this);
        HeaderText.Text = files.Count == 0
            ? $"Block {block:N0}  (LCN {startLcn:N0}–{startLcn + count - 1:N0}) — no mapped files in this range. Analyse the volume first, or the block is free / unmapped."
            : $"Block {block:N0}  (LCN {startLcn:N0}–{startLcn + count - 1:N0}) — {files.Count} file(s)";
        FileGrid.ItemsSource = files;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
