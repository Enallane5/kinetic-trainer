using Windows.Storage;
using Windows.Storage.Pickers;

namespace KineticTrainer.App.Services;

/// <summary>File pickers for this unpackaged app (they must be tied to the main window handle).</summary>
public static class WindowServices
{
    public static IntPtr MainWindowHandle { get; set; }

    public static async Task<IReadOnlyList<StorageFile>> PickOpenFilesAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        foreach (var e in extensions)
        {
            picker.FileTypeFilter.Add(e);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle);
        return await picker.PickMultipleFilesAsync();
    }

    public static async Task<StorageFile?> PickSaveFileAsync(string suggestedName, string typeLabel, string extension)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(typeLabel, [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle);
        return await picker.PickSaveFileAsync();
    }

    public static async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle);
        return await picker.PickSingleFolderAsync();
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "workout" : cleaned;
    }
}
