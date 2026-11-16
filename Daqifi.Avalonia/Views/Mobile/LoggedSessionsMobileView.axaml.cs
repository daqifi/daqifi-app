using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Daqifi.Desktop.Exporter;

namespace Daqifi.Avalonia.Views.Mobile;

public partial class LoggedSessionsMobileView : UserControl
{
    public LoggedSessionsMobileView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireResolver();

        // Detach fires only when the shell reassigns the content host (Storage → Channels/Profiles);
        // release the viewer + pending confirm immediately on that path. The IsVisible-only exit paths
        // (the Stream/home tab and the APP LOGS↔DEVICE LOGS pivot don't detach) are instead covered by
        // Reload()'s on-re-entry reset, so returning to Storage always shows the fresh list.
        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is LoggedSessionsMobileViewModel vm)
            {
                vm.ConfirmOverlay.Cancel();
                vm.CloseViewerCommand.Execute(null);
            }
        };
    }

    // The VM owns the export logic but not the platform picker; the view (which has
    // the TopLevel) supplies the resolvers via StorageProvider — the same
    // cross-platform pickers the desktop loggers use. A single session exports to a
    // picked FILE; "Export all" exports one CSV per session into a picked FOLDER
    // (mirrors the desktop, which writes {session}.csv per session into a directory).
    private void WireResolver()
    {
        if (DataContext is LoggedSessionsMobileViewModel vm)
        {
            vm.SaveFileResolver = SaveFileAsync;
            vm.SaveFolderResolver = SaveFolderAsync;
        }
    }

    // Both resolvers return the storage ITEM, never TryGetLocalPath(): on Android every pick
    // is a content:// URI with no path, and the old path-only resolvers returned null for it,
    // which the VM read as a cancel — so export wrote nothing and said nothing (#587). The VM
    // writes through the item's own stream (StorageExportWriter).
    private async Task<IStorageFile?> SaveFileAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) { return null; }

        return await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export logged data",
            // No extension in suggestedName: the picker appends DefaultExtension to it.
            SuggestedFileName = suggestedName,
            DefaultExtension = ExportFileNamer.CsvExtension,
            FileTypeChoices = new[]
            {
                // MimeTypes is what the Android picker filters and creates by; Patterns is
                // what the desktop and iOS pickers use.
                new FilePickerFileType("CSV")
                {
                    Patterns = new[] { "*." + ExportFileNamer.CsvExtension },
                    MimeTypes = new[] { "text/csv" },
                },
            },
        });
    }

    private async Task<IStorageFolder?> SaveFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) { return null; }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export all sessions to folder",
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0] : null;
    }
}
