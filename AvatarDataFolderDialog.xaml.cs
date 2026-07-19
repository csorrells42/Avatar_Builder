using System.IO;
using System.Windows;
using AvatarBuilder.Modules.Infrastructure;
using Microsoft.Win32;

namespace AvatarBuilder;

public partial class AvatarDataFolderDialog : Window
{
    public AvatarDataFolderDialog(string initialFolder)
    {
        InitializeComponent();
        SelectedFolder = initialFolder;
        UpdateFolderDisplay();
    }

    public string SelectedFolder { get; private set; }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        DarkWindowFrame.Apply(this);
    }

    private void ChooseFolderClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose avatar data folder",
            InitialDirectory = Directory.Exists(SelectedFolder) ? SelectedFolder : AppContext.BaseDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SelectedFolder = dialog.FolderName;
        UpdateFolderDisplay();
    }

    private void OkClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void UpdateFolderDisplay()
    {
        FolderPathTextBox.Text = SelectedFolder;
        FolderPathTextBox.CaretIndex = FolderPathTextBox.Text.Length;
        StorageCapacityText.Text = GetStorageCapacityLabel(SelectedFolder);
    }

    private static string GetStorageCapacityLabel(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrWhiteSpace(root))
            {
                return "Storage capacity unavailable: unknown drive.";
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return $"Storage capacity unavailable: {root} is not ready.";
            }

            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            var totalGb = drive.TotalSize / 1024d / 1024d / 1024d;
            var windowsRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var driveType = root.Equals(windowsRoot, StringComparison.OrdinalIgnoreCase)
                ? "Windows drive"
                : "off-system drive";
            return $"Storage: {drive.Name} {freeGb:0.0} GB free of {totalGb:0.0} GB ({driveType}).";
        }
        catch (Exception ex)
        {
            return $"Storage capacity unavailable: {ex.Message}";
        }
    }
}
