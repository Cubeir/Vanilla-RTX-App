using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using static Vanilla_RTX_App.MainWindow;

namespace Vanilla_RTX_App.Modules;

public static class Exporter
{

    public static async Task ExportMCPACK(string packFolderPath, string suggestedName)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Instance);
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeChoices.Add("Minecraft Pack", new List<string>() { ".mcpack" });
        picker.SuggestedFileName = suggestedName;
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        var unneededFiles = new[] { "contents.json", "textures_list.json" };
        foreach (var unneededFile in Directory.GetFiles(packFolderPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (unneededFiles.Contains(Path.GetFileName(unneededFile), StringComparer.OrdinalIgnoreCase))
                File.Delete(unneededFile);
        }

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"temp_{Guid.NewGuid()}.mcpack");

        try
        {
            using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                var baseFolder = Path.GetFileName(packFolderPath.TrimEnd(Path.DirectorySeparatorChar));
                foreach (var filePath in Directory.GetFiles(packFolderPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.Combine(baseFolder, Path.GetRelativePath(packFolderPath, filePath));
                    zip.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                }
            }

            if (!File.Exists(tempZipPath))
            {
                Trace.WriteLine($"Temporary .mcpack archive was deleted before writing to output.");
                return;
            }

            using var destStream = await file.OpenStreamForWriteAsync();
            using var srcStream = File.OpenRead(tempZipPath);
            await srcStream.CopyToAsync(destStream);

            Trace.WriteLine($"{suggestedName}.mcpack exported successfully.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to export {suggestedName}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Warning: Couldn't delete temp file: {ex.Message}");
            }
        }
    }
}
