using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ImageMagick;
using Windows.Storage.Pickers;
using WinRT.Interop;
using static Vanilla_RTX_App.MainWindow;

namespace Vanilla_RTX_App.Modules;

public static class Helpers
{
    /// <summary>
    /// Reads images of any given format, with an option to return opacity at maximum (retaining rgb data under 0 opacity pixels)
    /// </summary>
    public static Bitmap ReadImage(string imagePath, bool maxOpacity = false)
    {
        try
        {
            using var sourceImage = new MagickImage(imagePath);
            var width = (int)sourceImage.Width;
            var height = (int)sourceImage.Height;


            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (var sourcePixels = sourceImage.GetPixels())
            {

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var pixelData = sourcePixels.GetPixel(x, y);

                        byte r, g, b, a;

                        var hasAlpha = sourceImage.HasAlpha || sourceImage.ColorType == ColorType.GrayscaleAlpha || sourceImage.ColorType == ColorType.TrueColorAlpha;

                        if (sourceImage.ColorType == ColorType.Grayscale)
                        {
                            var gray = (byte)(pixelData[0] >> 8);
                            r = g = b = gray;
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.GrayscaleAlpha)
                        {
                            var gray = (byte)(pixelData[0] >> 8);
                            r = g = b = gray;
                            var originalAlpha = (byte)(pixelData[1] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColor)
                        {
                            r = (byte)(pixelData[0] >> 8);
                            g = (byte)(pixelData[1] >> 8);
                            b = (byte)(pixelData[2] >> 8);
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColorAlpha)
                        {
                            r = (byte)(pixelData[0] >> 8);
                            g = (byte)(pixelData[1] >> 8);
                            b = (byte)(pixelData[2] >> 8);
                            var originalAlpha = (byte)(pixelData[3] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.Palette)
                        {
                            r = (byte)(pixelData[0] >> 8);
                            g = (byte)(pixelData[1] >> 8);
                            b = (byte)(pixelData[2] >> 8);

                            if (hasAlpha && sourceImage.ChannelCount > 3)
                            {
                                var originalAlpha = (byte)(pixelData[3] >> 8);
                                a = maxOpacity ? (byte)255 : originalAlpha;
                            }
                            else
                            {
                                a = 255;
                            }
                        }
                        else
                        {
                            var channels = (int)sourceImage.ChannelCount;

                            r = channels > 0 ? (byte)(pixelData[0] >> 8) : (byte)0;
                            g = channels > 1 ? (byte)(pixelData[1] >> 8) : r;
                            b = channels > 2 ? (byte)(pixelData[2] >> 8) : r;

                            if (hasAlpha && channels > 3)
                            {
                                var originalAlpha = (byte)(pixelData[3] >> 8);
                                a = maxOpacity ? (byte)255 : originalAlpha;
                            }
                            else
                            {
                                a = 255;
                            }
                        }
                        var pixelColor = Color.FromArgb(a, r, g, b);
                        bitmap.SetPixel(x, y, pixelColor);
                    }
                }
            }

            return bitmap;
        }
        catch (Exception)
        {
            var errorBitmap = new Bitmap(512, 512, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(errorBitmap))
            {
                g.Clear(Color.Transparent);
                var squareSize = 256;
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 77, 172, 255)), 0, 0, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0, 35, 66)), squareSize, 0, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0, 35, 66)), 0, squareSize, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 77, 172, 255)), squareSize, squareSize, squareSize, squareSize);
            }
            return errorBitmap;
        }
    }
    /// <summary>
    /// Write a bitmap to a path as raw, pure targa with 4 channels, 8 bit per channel
    /// </summary>
    public static void WriteImageAsTGA(Bitmap bitmap, string outputPath)
    {
        try
        {
            var width = bitmap.Width;
            var height = bitmap.Height;

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);
            // TGA
            writer.Write((byte)0);    // ID Length
            writer.Write((byte)0);    // Color Map Type (0 = no color map)
            writer.Write((byte)2);    // Image Type (2 = uncompressed RGB)
            writer.Write((ushort)0);  // Color Map First Entry Index
            writer.Write((ushort)0);  // Color Map Length
            writer.Write((byte)0);    // Color Map Entry Size
            writer.Write((ushort)0);  // X-origin
            writer.Write((ushort)0);  // Y-origin
            writer.Write((ushort)width);  // Width
            writer.Write((ushort)height); // Height
            writer.Write((byte)32);       // Pixel Depth (32-bit RGBA)
            writer.Write((byte)8);        // Image Descriptor (default origin, 8-bit alpha)

            for (var y = height - 1; y >= 0; y--) // TGA is bottom-up by default
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    writer.Write(pixel.B);
                    writer.Write(pixel.G);
                    writer.Write(pixel.R);
                    writer.Write(pixel.A);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error writing direct TGA to {outputPath}: {ex.Message}");
            throw;
        }
    }


    public static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    static Helpers()
    {
        SharedHttpClient.DefaultRequestHeaders.Add("User-Agent", $"vanilla_rtx_app/{TunerVariables.appVersion}");
        Trace.WriteLine("[HttpsHelper] SharedHttpClient configured");
    }
    /// <summary>
    /// Downloads a file with progress tracking and retry logic.
    /// Uses the shared HttpClient which is pre-configured.
    /// For custom timeout/headers, pass a custom HttpClient.
    /// </summary>
    public static async Task<(bool, string?)> Download(
        string url,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        var client = httpClient ?? SharedHttpClient;
        var retries = 3;

        while (retries-- > 0)
        {
            try
            {
                // === DOWNLOAD ===
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                Log("Starting Download.", LogLevel.Lengthy);

                var totalBytes = response.Content.Headers.ContentLength;
                if (!totalBytes.HasValue)
                    Log("Total file size unknown. Progress will be logged as total downloaded (in MegaBytes).", LogLevel.Informational);

                // === FILENAME EXTRACTION AND SANITIZATION ===
                string fileName;
                if (response.Content.Headers.ContentDisposition?.FileName != null)
                {
                    fileName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
                }
                else
                {
                    fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        fileName = $"download_{Guid.NewGuid():N}";
                        Log($"No valid filename found, using random name: {fileName}", LogLevel.Informational);
                    }
                    else
                    {
                        Log("File name: " + fileName, LogLevel.Informational);
                    }
                }

                // sanitize filename
                fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
                if (fileName.Length > 128) fileName = fileName.Substring(0, 128);

                // === LOCATION RESOLUTION ===
                string? savingLocation = null;

                try
                {
                    var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                    var downloadDir = Path.Combine(localFolder, "Downloads");
                    Directory.CreateDirectory(downloadDir);

                    var finalPath = Path.Combine(downloadDir, fileName);
                    var counter = 1;
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    var extension = Path.GetExtension(fileName);

                    while (File.Exists(finalPath))
                    {
                        var newFileName = $"{fileNameWithoutExt}-{counter}{extension}";
                        finalPath = Path.Combine(downloadDir, newFileName);
                        counter++;
                    }

                    savingLocation = finalPath;
                    Log($"Save location: {savingLocation}", LogLevel.Informational);
                }
                catch (Exception ex)
                {
                    Log($"Failed to establish save location: {ex.Message}", LogLevel.Error);
                    savingLocation = null;
                }

                if (savingLocation == null)
                {
                    Log("No writable location found for download.", LogLevel.Error);
                    return (false, null);
                }

                // === DOWNLOAD WITH PROGRESS TRACKING ===
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(savingLocation, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int read;
                double lastLoggedProgress = 0;
                var lastLoggedMB = 0;

                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;

                    if (totalBytes.HasValue)
                    {
                        var progress = (double)totalRead / totalBytes.Value * 100;
                        if (progress - lastLoggedProgress >= 10 || progress >= 100)
                        {
                            lastLoggedProgress = progress;
                            Log($"Download Progress: {progress:0}%", LogLevel.Informational);
                        }
                    }
                    else
                    {
                        var currentMB = (int)(totalRead / (1024 * 1024));
                        if (currentMB > lastLoggedMB)
                        {
                            lastLoggedMB = currentMB;
                            Log($"Download Progress: {currentMB} MB", LogLevel.Informational);
                        }
                    }
                }

                Log("Download finished successfully.", LogLevel.Success);
                return (true, savingLocation);
            }
            catch (OperationCanceledException ex) when (!(ex is TaskCanceledException) || ex.InnerException is not TimeoutException)
            {
                Log("Download cancelled by user.", LogLevel.Informational);
                return (false, null);
            }
            catch (HttpRequestException ex) when (retries > 0)
            {
                Log($"Transient error: {ex.Message}. Retrying...", LogLevel.Warning);
                await Task.Delay(1000, cancellationToken);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && retries > 0)
            {
                Log("Request timed out. Retrying...", LogLevel.Warning);
                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                Log($"Error during download: {ex.Message}", LogLevel.Error);
                return (false, null);
            }
        }

        Log("Download failed after multiple attempts.", LogLevel.Error);
        return (false, null);
    }


    /// <summary>
    /// Copies a set of files using a single elevated batch script (one UAC prompt for all files).
    /// Returns true only if the elevated process exits with code 0.
    /// </summary>
    /// <param name="filesToReplace">List of (sourcePath, destPath) pairs to copy.</param>
    /// <param name="logPrefix">Tag used in Trace output, e.g. "[BetterRTX]", "[DLSS]", "[LUTManager]".</param>
    /// <param name="tempFilePrefix">Prefix for the temp batch file name, to keep temp files identifiable per-feature.</param>
    public static async Task<bool> ReplaceFilesWithElevation(List<(string sourcePath, string destPath)> filesToReplace,
        string logPrefix = "[Helpers]", string tempFilePrefix = "file_replace")
    {
        try
        {
            if (filesToReplace == null || filesToReplace.Count == 0)
            {
                Trace.WriteLine($"{logPrefix} ReplaceFilesWithElevation called with no files — nothing to do");
                return false;
            }

            return await Task.Run(() =>
            {
                var scriptLines = new List<string> { "@echo off" };
                foreach (var (sourcePath, destPath) in filesToReplace)
                    scriptLines.Add($"copy /Y \"{sourcePath}\" \"{destPath}\" >nul 2>&1");
                scriptLines.Add("exit %ERRORLEVEL%");

                var batchScript = string.Join("\r\n", scriptLines);
                var tempBatchPath = Path.Combine(
                    Path.GetTempPath(),
                    $"{tempFilePrefix}_{Guid.NewGuid():N}.bat");

                File.WriteAllText(tempBatchPath, batchScript);

                Trace.WriteLine($"{logPrefix} Batch script: {tempBatchPath}");
                Trace.WriteLine($"{logPrefix} Contents:\n{batchScript}");

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{tempBatchPath}\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        process.WaitForExit();
                        Trace.WriteLine($"{logPrefix} Exit code: {process.ExitCode}");
                        return process.ExitCode == 0;
                    }

                    Trace.WriteLine($"{logPrefix} Process.Start returned null");
                    return false;
                }
                finally
                {
                    try
                    {
                        Thread.Sleep(300);
                        if (File.Exists(tempBatchPath))
                            File.Delete(tempBatchPath);
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"{logPrefix} Error in ReplaceFilesWithElevation: {ex.Message}");
            return false;
        }
    }



    /// <summary>
    /// Shortns it too
    /// </summary>
    public static string SanitizePathForDisplay(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return fullPath;

        try
        {
            // Find LocalState in the path
            int localStateIndex = fullPath.IndexOf("LocalState", StringComparison.OrdinalIgnoreCase);

            if (localStateIndex > 0)
            {
                // Get everything after "LocalState"
                string afterLocalState = fullPath.Substring(localStateIndex);
                return $"Data\\{afterLocalState}";
            }

            // If LocalState not found, just return the filename and parent folder
            var fileName = Path.GetFileName(fullPath);
            var parentFolder = Path.GetFileName(Path.GetDirectoryName(fullPath));
            return $"...\\{parentFolder}\\{fileName}";
        }
        catch
        {
            // Fallback to just showing the last two segments
            try
            {
                var fileName = Path.GetFileName(fullPath);
                var parentFolder = Path.GetFileName(Path.GetDirectoryName(fullPath));
                return $"...\\{parentFolder}\\{fileName}";
            }
            catch
            {
                return fullPath;
            }
        }
    }


    /// <summary>
    /// A custom implementation of generating a proper texture set, utilizes the custom implementation of TextureSetHelpers class in Processor.cs
    /// </summary>
    public static void GenerateTexturesLists(string rootDirectory)
    {
        static string FormatMinecraftJson(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return "[]";
            var formattedItems = paths.Select(path => $"    \"{path}\"");
            return "[\n" + string.Join(",\n", formattedItems) + "\n]";
        }

        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"Directory not found: {rootDirectory}");

        // ── Find all "textures" directories (unchanged) ───────────────────────────
        var texturesDirectories = Directory
            .GetDirectories(rootDirectory, "textures", SearchOption.AllDirectories)
            .ToList();

        if (Path.GetFileName(rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("textures", StringComparison.OrdinalIgnoreCase))
            texturesDirectories.Add(rootDirectory);

        if (texturesDirectories.Count == 0)
            return;

        string[] imageExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

        foreach (string texturesDir in texturesDirectories)
        {
            // ── Collect all non-color file paths to exclude ───────────────────────
            //
            // ResolveTextureSets validates every texture set in one pass and gives us
            // structured access to each layer. We exclude any real-file path that
            // belongs to a non-color layer (MER/MERS, normal, heightmap).
            // Inline layers (RGB arrays / hex values) have no FilePath, so nothing
            // is added to the exclusion set for them.

            var pbrTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var resolvedSets = TextureSetHelper.ResolveTextureSets(texturesDir);

            foreach (var rs in resolvedSets)
            {
                // MER / MERS layer
                if (rs.Mer is { IsInline: false, FilePath: not null } mer)
                    pbrTextures.Add(mer.FilePath);

                // Normal or heightmap layer
                if (rs.NormalOrHeight is { IsInline: false, FilePath: not null } normalOrHeight)
                    pbrTextures.Add(normalOrHeight.FilePath);
            }

            // ── Collect all image files (unchanged) ───────────────────────────────
            var imageFiles = new List<string>();
            foreach (string ext in imageExtensions)
            {
                imageFiles.AddRange(Directory.GetFiles(texturesDir, $"*{ext}", SearchOption.AllDirectories));
                imageFiles.AddRange(Directory.GetFiles(texturesDir, $"*{ext.ToUpper()}", SearchOption.AllDirectories));
            }

            // ── Build relative paths, filtering out non-color PBR textures ────────
            var filteredPaths = new List<string>();
            foreach (string filePath in imageFiles.Distinct())
            {
                if (pbrTextures.Contains(filePath))
                    continue;

                string relativePath = Path.GetRelativePath(texturesDir, filePath).Replace('\\', '/');
                string pathWithoutExtension = Path.ChangeExtension(relativePath, null);
                filteredPaths.Add("textures/" + pathWithoutExtension);
            }

            filteredPaths.Sort();

            File.WriteAllText(
                Path.Combine(texturesDir, "textures_list.json"),
                FormatMinecraftJson(filteredPaths));
        }
    }


    /// <summary>
    /// Checks if Minecraft.Windows process is running, returns true if so
    /// </summary>
    public static bool IsMinecraftRunning()
    {
        var mcProcesses = Process.GetProcessesByName("Minecraft.Windows");
        return mcProcesses.Length > 0;
    }

    /// <summary>
    /// Returns one of 3 special occasion names (me and my loved one's "birthday"s, "christmas", or "pumpkin" during weekends of October)
    /// </summary>
    public static string? GetSpecialOccasionName()
    {
        var date = DateTime.Today;
        if (date.Month == 4 && date.Day >= 21 && date.Day <= 23)
            return "birthday";
        if (date.Month == 10 && (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday))
            return "pumpkin";
        if ((date.Month == 12 && date.Day >= 23) || (date.Month == 1 && date.Day <= 7))
            return "christmas";
        return null;
    }


    /// <summary>
    /// Additional helper to do a thing only once per runtime, use RanOnceFlag.Set("key") to set a flag with a unique key.
    /// </summary>
    public static class RuntimeFlags
    {
        private static readonly HashSet<string> _flags = new();

        public static bool Has(string key) => _flags.Contains(key); // Below does the same as this one if already set

        public static bool Set(string key)
        {
            try
            {
                if (_flags.Contains(key))
                    return false;

                _flags.Add(key);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RUNETIMEFLAGS] Something went wrong: {ex.ToString}");
                return false;
            }
        }

        public static bool Unset(string key) => _flags.Remove(key);
    }
}

# region MC GDK LOCATOR TOOLS

/// <summary>
/// Provides tools for locating Minecraft (Bedrock) and Minecraft Preview installations.
/// Handles caching, validation, system-wide searching, and manual selection.
///
/// Contract: every path returned or cached by this class is the PHYSICAL directory
/// containing Minecraft.Windows.exe — i.e. the Content subfolder of the install root.
/// Callers reference files as Path.Combine(installPath, "filename") directly.
/// No symlinks or junctions are ever stored — all paths are resolved to physical targets.
///
/// Edition detection (Preview vs Stable) is authoritative, not name-based: every
/// GDK Minecraft install ships a MicrosoftGame.Config next to the exe whose
/// <Identity Name="..."/> attribute is "Microsoft.MinecraftUWP" (stable) or
/// "Microsoft.MinecraftWindowsBeta" (preview). This value is baked in by Mojang/Microsoft
/// and is independent of folder names, GUIDs, drive letters, or which launcher installed it.
/// Folder names and known package GUIDs are used only as fast-path optimizations to try
/// first — they are never required for correctness.
///
/// Location flow:
///   Phase 1 (startup, fast):
///     Cache check → Stage 0: PackageManager → Stage 1: Common locations
///   Phase 2 (async, slow):
///     System-wide recursive search across all fixed drives — matches on the
///     presence of Minecraft.Windows.exe + a MicrosoftGame.Config with the
///     correct Identity, never on folder name.
///   Phase 3 (manual):
///     User picks Minecraft.Windows.exe — directory is validated and cached
/// </summary>
public static class MinecraftGDKLocator
{
    public const string MinecraftFolderName = "Minecraft for Windows";
    public const string MinecraftPreviewFolderName = "Minecraft Preview for Windows";
    public const string MinecraftExecutableName = "Minecraft.Windows.exe";
    private const string GameConfigFileName = "MicrosoftGame.Config";
    private const int MaxSearchDepth = 9;

    // Package family names — stable post-GDK (1.21.120+)
    private const string MinecraftStablePackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    private const string MinecraftPreviewPackageFamilyName = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";

    // MicrosoftGame.Config <Identity Name="..."/> values — the authoritative,
    // folder-name-independent way to tell stable and preview apart. These are
    // the same identity strings the package family names above are built from,
    // and they have remained unchanged even through the "Beta" → "Preview" rebrand.
    private const string MinecraftStableIdentityName = "Microsoft.MinecraftUWP";
    private const string MinecraftPreviewIdentityName = "Microsoft.MinecraftWindowsBeta";

    // Known Microsoft Store install GUIDs used in place of friendly folder names
    // by some install paths. Treated as fully interchangeable with the friendly
    // names below — both are just fast-path hints, never a requirement.
    private const string MinecraftStableStoreGuid = "7792D9CE-355A-493C-AFBD-768F4A77C3B0";
    private const string MinecraftPreviewStoreGuid = "98BD2335-9B01-4E4C-BD05-CCC01614078B";

    private static readonly HashSet<string> FoldersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System32", "WinSxS", "$Recycle.Bin", "ProgramData",
        "AppData", "Recovery", "System Volume Information", "Config.Msi",
        "Windows.old", "PerfLogs", "Temp", "tmp", "Program Files (x86)",
        "MSOCache", "OneDriveTemp"
    };

    // -------------------------------------------------------------------------
    // PUBLIC API
    // -------------------------------------------------------------------------

    /// <summary>
    /// PHASE 1: Quick validation of cached paths and common locations.
    /// Called on app startup. Self-contained and fast.
    /// Validates both Minecraft stable and Preview installations.
    /// </summary>
    public static void ValidateAndUpdateCachedLocations()
    {
        Trace.WriteLine("=== PHASE 1: Quick Validation Starting ===");

        ValidateAndUpdateSingleInstallation(
            isPreview: false,
            cachedPath: TunerVariables.Persistent.MinecraftInstallPath,
            updateCache: (path) => TunerVariables.Persistent.MinecraftInstallPath = path
        );

        ValidateAndUpdateSingleInstallation(
            isPreview: true,
            cachedPath: TunerVariables.Persistent.MinecraftPreviewInstallPath,
            updateCache: (path) => TunerVariables.Persistent.MinecraftPreviewInstallPath = path
        );

        Trace.WriteLine("=== PHASE 1 Complete ===");
    }

    /// <summary>
    /// Quick re-validation of a cached path before use.
    /// Called by feature windows before trusting the cache.
    /// Also detects and evicts stale symlink paths, and evicts paths whose
    /// edition no longer matches what's expected (e.g. after a manual swap).
    /// </summary>
    public static bool RevalidateCachedPath(string? cachedPath, bool expectedPreview)
    {
        if (string.IsNullOrEmpty(cachedPath))
            return false;

        if (!Directory.Exists(cachedPath))
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path no longer exists: {cachedPath}");
            return false;
        }

        if (!IsValidExecutableDirectory(cachedPath))
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path no longer valid: {cachedPath}");
            return false;
        }

        // Evict if the cached path is still a symlink — force re-discovery
        // so the physical path gets written to cache instead.
        var resolved = ResolveToPhysicalPath(cachedPath);
        if (!resolved.Equals(cachedPath, StringComparison.OrdinalIgnoreCase))
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path is a symlink — evicting so physical path gets cached: {resolved}");
            return false;
        }

        // Authoritative edition check via MicrosoftGame.Config. If the config is
        // missing or unreadable we don't evict on that basis alone (degrade gracefully —
        // see TryGetEditionFromGameConfig), but a confirmed mismatch is disqualifying.
        var detectedEdition = TryGetEditionFromGameConfig(resolved);
        if (detectedEdition.HasValue && detectedEdition.Value != expectedPreview)
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path edition mismatch (expected Preview={expectedPreview}, found Preview={detectedEdition.Value}) — evicting");
            return false;
        }

        return true;
    }

    /// <summary>
    /// PHASE 2: Deep system-wide search for Minecraft installation.
    /// Only searches for the version the user is targeting.
    /// Can be cancelled when the user initiates manual selection.
    /// Returns the physical directory containing Minecraft.Windows.exe.
    ///
    /// Matching is based entirely on file contents (exe + MicrosoftGame.Config
    /// Identity), never on folder name — friendly names and known GUIDs are only
    /// used as a priority pass to find common cases fast.
    /// </summary>
    public static async Task<string?> SearchForMinecraftAsync(bool searchForPreview, CancellationToken cancellationToken)
    {
        Trace.WriteLine($"=== PHASE 2: Deep System Search Starting (Preview={searchForPreview}) ===");

        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();

            Trace.WriteLine($"[GDKLocator] Found {drives.Count} fixed drives to search");

            foreach (var drive in drives)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Trace.WriteLine("[GDKLocator] Search cancelled by user");
                    return null;
                }

                Trace.WriteLine($"[GDKLocator] Scanning drive: {drive.Name}");

                // Priority pass: check high-probability locations (friendly names + known GUIDs)
                foreach (var priorityPath in GetCommonLocations(searchForPreview, drive))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    if (IsValidExecutableDirectoryForEdition(priorityPath, searchForPreview))
                    {
                        Trace.WriteLine($"[GDKLocator] Found at priority location: {priorityPath}");
                        CacheInstallation(searchForPreview, priorityPath);
                        return priorityPath;
                    }
                }

                // Deep recursive search of this drive — matches on exe + config identity only,
                // completely independent of folder naming.
                var foundPath = await RecursiveSearchAsync(
                    drive.Name,
                    searchForPreview,
                    currentDepth: 0,
                    cancellationToken
                );

                if (foundPath != null)
                {
                    Trace.WriteLine($"[GDKLocator] Found via deep search: {foundPath}");
                    CacheInstallation(searchForPreview, foundPath);
                    return foundPath;
                }
            }

            Trace.WriteLine("[GDKLocator] Target not found on any drive");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error during system search: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// PHASE 3: Manual selection — user picks a folder near the installation
    /// (folder picker, not file picker: the exe itself may sit in a
    /// permission-protected directory that the OS won't allow the app to "open,"
    /// even though only the path is needed — folders don't carry that restriction).
    ///
    /// Tolerant of imprecision: accepts the exact folder containing Minecraft.Windows.exe,
    /// or a folder one level shallower (the install root, whose child folder — named
    /// anything, including a GUID — holds the exe). This mirrors the leniency
    /// MinecraftUserDataLocator gives when accepting Shared/Users subfolders.
    /// Edition is verified via MicrosoftGame.Config, not folder name.
    /// </summary>
    public static async Task<string?> LocateMinecraftManuallyAsync(bool isPreview, IntPtr windowHandle)
    {
        Trace.WriteLine($"=== PHASE 3: Manual Selection Starting (Preview={isPreview}) ===");

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");

            InitializeWithWindow.Initialize(picker, windowHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                Trace.WriteLine("[GDKLocator] User cancelled folder selection");
                return null;
            }

            Trace.WriteLine($"[GDKLocator] User selected: {folder.Path}");

            var resolvedSelection = ResolveToPhysicalPath(folder.Path);
            if (!resolvedSelection.Equals(folder.Path, StringComparison.OrdinalIgnoreCase))
                Trace.WriteLine($"[GDKLocator] Resolved selection: {folder.Path} → {resolvedSelection}");

            var exeDirectory = FindExecutableDirectoryNearby(resolvedSelection);
            if (exeDirectory == null)
            {
                Trace.WriteLine($"[GDKLocator] Could not find {MinecraftExecutableName} in or one level under the selected folder");
                return null;
            }

            // Authoritative edition check via MicrosoftGame.Config.
            var detectedEdition = TryGetEditionFromGameConfig(exeDirectory);
            if (detectedEdition.HasValue)
            {
                if (detectedEdition.Value != isPreview)
                {
                    var foundName = detectedEdition.Value ? "Preview" : "Stable";
                    var expectedName = isPreview ? "Preview" : "Stable";
                    Trace.WriteLine($"[GDKLocator] Selected wrong version — MicrosoftGame.Config identifies this as {foundName}, expected {expectedName}");
                    return null;
                }
            }
            else
            {
                // No usable config — soft folder-name guard as last resort, same as before.
                var unexpectedFolderName = isPreview ? MinecraftFolderName : MinecraftPreviewFolderName;
                var installRoot = Directory.GetParent(exeDirectory)?.Name ?? string.Empty;
                if (installRoot.Equals(unexpectedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine($"[GDKLocator] Selected wrong version — install root is: {installRoot}");
                    return null;
                }
                Trace.WriteLine("[GDKLocator] MicrosoftGame.Config unavailable — proceeding on unverified edition (folder name didn't indicate a mismatch)");
            }

            Trace.WriteLine($"[GDKLocator] Valid installation selected: {exeDirectory}");
            CacheInstallation(isPreview, exeDirectory);
            return exeDirectory;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error during manual selection: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Looks for Minecraft.Windows.exe directly inside the selected folder, or one
    /// level deeper — tolerating the user having selected the install root instead
    /// of the exe's own folder. No name assumption on that child folder: it could be
    /// "Content", a GUID, or anything a third-party launcher decided to call it.
    /// Each candidate is symlink-resolved before being checked, since a subfolder
    /// can itself turn out to be a junction. This is a bounded, one-hop convenience —
    /// not a search; Phase 2 already owns unbounded discovery.
    /// </summary>
    private static string? FindExecutableDirectoryNearby(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
            return null;

        // Direct hit — selected folder already contains the exe
        if (IsValidExecutableDirectory(selectedPath))
            return selectedPath;

        // One level deeper — selected folder was probably the install root
        try
        {
            foreach (var subdir in Directory.GetDirectories(selectedPath))
            {
                var resolvedSubdir = ResolveToPhysicalPath(subdir);
                if (IsValidExecutableDirectory(resolvedSubdir))
                    return resolvedSubdir;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error scanning subfolders of {selectedPath}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Returns common installation locations for a given drive (or all fixed drives).
    /// Includes both friendly folder names and known Microsoft Store GUIDs — the two
    /// are fully interchangeable as far as this locator is concerned, since some
    /// installers use one and some use the other. Returns the Content subdirectory
    /// directly — the directory where the exe lives.
    /// </summary>
    public static IEnumerable<string> GetCommonLocations(bool isPreview, DriveInfo? onlyDrive = null)
    {
        var friendlyFolder = isPreview ? MinecraftPreviewFolderName : MinecraftFolderName;
        var storeGuid = isPreview ? MinecraftPreviewStoreGuid : MinecraftStableStoreGuid;

        var drives = onlyDrive != null
            ? new[] { onlyDrive }
            : DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);

        foreach (var drive in drives)
        {
            var root = drive.RootDirectory.FullName;

            // Xbox App install, friendly name
            yield return Path.Combine(root, "XboxGames", friendlyFolder, "Content");
            // Direct Microsoft Store install, GUID-named — fully equivalent to the friendly name
            yield return Path.Combine(root, "XboxGames", storeGuid, "Content");
            // Some installs land directly under Program Files
            yield return Path.Combine(root, "Program Files", "Microsoft Games", friendlyFolder, "Content");
        }
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Core Phase 1 logic for a single edition.
    /// Order: cache check → Stage 0 PackageManager → Stage 1 common locations.
    /// Symlink resolution and edition verification are applied at every point a path is accepted.
    /// </summary>
    private static void ValidateAndUpdateSingleInstallation(
        bool isPreview,
        string? cachedPath,
        Action<string?> updateCache)
    {
        var versionName = isPreview ? "Preview" : "Stable";
        Trace.WriteLine($"[GDKLocator] Validating {versionName} Minecraft...");

        // Cache check
        if (!string.IsNullOrEmpty(cachedPath))
        {
            Trace.WriteLine($"[GDKLocator] Cached path: {cachedPath}");

            if (RevalidateCachedPath(cachedPath, isPreview))
            {
                // RevalidateCachedPath already confirmed exe + edition; only the
                // symlink-resolution re-cache case needs writing back here, and
                // RevalidateCachedPath would have returned false for that case
                // (forcing this branch to fall through to rediscovery), so a true
                // result here means the cache is genuinely already correct as-is.
                Trace.WriteLine($"[GDKLocator] Cache valid for {versionName}");
                return;
            }

            Trace.WriteLine($"[GDKLocator] Cache invalid for {versionName}, clearing");
            updateCache(null);

            // The cache might have been invalid purely because it was a symlink
            // pointing at an otherwise-correct physical path — try that quick
            // resolve-and-recache before falling all the way through to Stage 0/1.
            if (Directory.Exists(cachedPath) && IsValidExecutableDirectory(cachedPath))
            {
                var resolved = ResolveToPhysicalPath(cachedPath);
                var resolvedEdition = TryGetEditionFromGameConfig(resolved);
                if (!resolvedEdition.HasValue || resolvedEdition.Value == isPreview)
                {
                    Trace.WriteLine($"[GDKLocator] Cache was a symlink — re-caching physical path: {resolved}");
                    updateCache(resolved);
                    return;
                }
            }
        }
        else
        {
            Trace.WriteLine($"[GDKLocator] No cached path for {versionName}");
        }

        // STAGE 0: PackageManager — authoritative OS query, instant
        var packagePath = TryGetInstallPathFromPackageManager(isPreview);
        if (packagePath != null)
        {
            Trace.WriteLine($"[GDKLocator] Found {versionName} via PackageManager: {packagePath}");
            updateCache(packagePath);
            return;
        }

        // STAGE 1: Common locations across all drives (friendly names + known GUIDs)
        foreach (var location in GetCommonLocations(isPreview))
        {
            Trace.WriteLine($"[GDKLocator] Checking common location: {location}");
            if (IsValidExecutableDirectoryForEdition(location, isPreview))
            {
                Trace.WriteLine($"[GDKLocator] Found {versionName} at common location: {location}");
                updateCache(location);
                return;
            }
        }

        Trace.WriteLine($"[GDKLocator] {versionName} not found in Phase 1");
    }

    /// <summary>
    /// STAGE 0: Query PackageManager for the game's registered install location.
    /// PackageManager returns the WindowsApps junction — resolved to the physical
    /// Content directory (where Minecraft.Windows.exe lives) before returning.
    /// </summary>
    private static string? TryGetInstallPathFromPackageManager(bool isPreview)
    {
        try
        {
            var familyName = isPreview ? MinecraftPreviewPackageFamilyName : MinecraftStablePackageFamilyName;
            Trace.WriteLine($"[GDKLocator] Querying PackageManager for: {familyName}");

            var packageManager = new Windows.Management.Deployment.PackageManager();
            var packages = packageManager.FindPackagesForUser(string.Empty, familyName);

            foreach (var package in packages)
            {
                var installLocation = package.InstalledLocation?.Path;
                if (string.IsNullOrEmpty(installLocation))
                    continue;

                Trace.WriteLine($"[GDKLocator] PackageManager returned: {installLocation}");

                var resolvedLocation = ResolveToPhysicalPath(installLocation);
                Trace.WriteLine($"[GDKLocator] Resolved to physical path: {resolvedLocation}");

                if (IsValidExecutableDirectory(resolvedLocation))
                {
                    Trace.WriteLine("[GDKLocator] Executable found at resolved path");
                    return resolvedLocation;
                }

                var contentSubdir = Path.Combine(resolvedLocation, "Content");
                if (IsValidExecutableDirectory(contentSubdir))
                {
                    Trace.WriteLine($"[GDKLocator] Executable found in Content subdir: {contentSubdir}");
                    return contentSubdir;
                }
            }

            Trace.WriteLine($"[GDKLocator] PackageManager: no valid install found for {familyName}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.WriteLine($"[GDKLocator] PackageManager access denied: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] PackageManager query failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves a path to its physical target, following symlinks/junctions to the end.
    /// Returns the original path unchanged if it is not a link or resolution fails.
    /// Safe to call on any path — non-links are a no-op.
    /// </summary>
    private static string ResolveToPhysicalPath(string path)
    {
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved))
            {
                Trace.WriteLine($"[GDKLocator] Symlink resolved: {path} → {resolved}");
                return resolved;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] ResolveLinkTarget failed for {path}: {ex.Message}");
        }

        return path;
    }

    /// <summary>
    /// Returns true if the directory exists and directly contains Minecraft.Windows.exe.
    /// This is the canonical validity check — the contract path always satisfies this.
    /// Does NOT verify edition; use IsValidExecutableDirectoryForEdition when the
    /// caller cares which edition it is.
    /// </summary>
    private static bool IsValidExecutableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return File.Exists(Path.Combine(path, MinecraftExecutableName));
    }

    /// <summary>
    /// Returns true if the directory contains Minecraft.Windows.exe AND its
    /// MicrosoftGame.Config identifies it as the requested edition. If the config
    /// is missing or unparseable, this degrades to exe-presence only — we never
    /// want a missing/corrupt config to make an otherwise-good install invisible.
    /// </summary>
    private static bool IsValidExecutableDirectoryForEdition(string path, bool expectedPreview)
    {
        if (!IsValidExecutableDirectory(path))
            return false;

        var detected = TryGetEditionFromGameConfig(path);
        return !detected.HasValue || detected.Value == expectedPreview;
    }

    /// <summary>
    /// Reads MicrosoftGame.Config next to the exe and parses its
    /// &lt;Identity Name="..."/&gt; attribute to authoritatively determine whether
    /// this install is Preview or Stable. This identity string is the same one the
    /// package family name is built from, is independent of folder naming or which
    /// installer placed it there, and has survived the "Beta" → "Preview" rebrand
    /// unchanged.
    ///
    /// Returns true for Preview, false for Stable, or null if the config is missing,
    /// unreadable, or doesn't contain a recognized identity (callers should treat
    /// null as "unknown" and fall back to other signals rather than rejecting outright).
    /// </summary>
    private static bool? TryGetEditionFromGameConfig(string executableDirectory)
    {
        try
        {
            var configPath = Path.Combine(executableDirectory, GameConfigFileName);
            if (!File.Exists(configPath))
                return null;

            var doc = XDocument.Load(configPath);
            var identityName = doc.Root?
                .Element("Identity")?
                .Attribute("Name")?
                .Value;

            if (string.IsNullOrEmpty(identityName))
                return null;

            if (identityName.Equals(MinecraftPreviewIdentityName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (identityName.Equals(MinecraftStableIdentityName, StringComparison.OrdinalIgnoreCase))
                return false;

            // Recognized config, but an identity we don't know — don't guess.
            Trace.WriteLine($"[GDKLocator] Unrecognized MicrosoftGame.Config Identity: {identityName}");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Failed to read/parse {GameConfigFileName} at {executableDirectory}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Recursively searches a directory tree for a Minecraft install of the requested
    /// edition. Matching is based entirely on directory contents — Minecraft.Windows.exe
    /// plus a MicrosoftGame.Config confirming the edition — never on folder name. This
    /// makes the fallback genuinely unconditional: GUID folders, third-party launcher
    /// naming, anything goes, as long as the files are really there.
    /// Used in Phase 2 only. Respects FoldersToSkip and CancellationToken.
    /// </summary>
    private static async Task<string?> RecursiveSearchAsync(
        string searchPath,
        bool searchForPreview,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        if (currentDepth >= MaxSearchDepth || cancellationToken.IsCancellationRequested)
            return null;

        try
        {
            // Test this directory directly — exe presence + confirmed edition.
            // Unlike the old folder-name-gated approach, every directory visited
            // is tested, not just ones matching a known name.
            if (IsValidExecutableDirectoryForEdition(searchPath, searchForPreview))
                return searchPath;

            var subdirectories = await Task.Run(() =>
            {
                try { return Directory.GetDirectories(searchPath); }
                catch { return Array.Empty<string>(); }
            }, cancellationToken);

            foreach (var subdir in subdirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                if (FoldersToSkip.Contains(Path.GetFileName(subdir)))
                    continue;

                var result = await RecursiveSearchAsync(subdir, searchForPreview, currentDepth + 1, cancellationToken);
                if (result != null)
                    return result;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error searching {searchPath}: {ex.Message}");
        }

        return null;
    }

    private static void CacheInstallation(bool isPreview, string path)
    {
        if (isPreview)
        {
            TunerVariables.Persistent.MinecraftPreviewInstallPath = path;
            Trace.WriteLine($"[GDKLocator] Cached Preview installation: {path}");
        }
        else
        {
            TunerVariables.Persistent.MinecraftInstallPath = path;
            Trace.WriteLine($"[GDKLocator] Cached Stable installation: {path}");
        }
    }
}
#endregion


# region MC USER DATA LOCATOR TOOLS

/// <summary>
/// Centralizes discovery and validation of Minecraft's GDK user data root —
/// the folder that contains worlds, options, resource packs, and the Shared tree.
///
/// Contract: the path stored in TunerVariables.Persistent.MinecraftDataPath (and
/// MinecraftPreviewDataPath) is always the "Minecraft Bedrock" or "Minecraft Bedrock
/// Preview" root folder — the one that directly contains a "Users" subfolder.
/// All deeper paths (com.mojang, resource_packs, options.txt) are derived from this
/// root on demand via the helper methods below.
///
/// Unlike GDKLocator, there is no exe or config file to serve as an absolute gospel
/// here — validation is based on folder structure (presence of the "Users" subfolder).
/// If the default AppData location is absent, we cannot reliably auto-discover an
/// alternative (third-party launchers like LeviLauncher can put this anywhere), so
/// we surface that as a user-actionable warning rather than attempting a blind search.
///
/// The result of the last validation is exposed as a simple bool per edition so that
/// the main window and any other caller can gate features without re-checking the path
/// themselves.
/// </summary>
public static class MinecraftUserDataLocator
{
    // ── Folder names ──────────────────────────────────────────────────────────
    public const string StableRootFolderName = "Minecraft Bedrock";
    public const string PreviewRootFolderName = "Minecraft Bedrock Preview";

    // ── Internal sub-paths ────────────────────────────────────────────────────
    private const string UsersFolderName = "Users";
    private static readonly string SharedComMojangSubPath = Path.Combine("Shared", "games", "com.mojang");
    private const string ResourcePacksFolderName = "resource_packs";
    private const string DevResourcePacksFolderName = "development_resource_packs";
    private const string OptionsFileName = "options.txt";

    // ── Last-known validation state (set by ValidateAndUpdateCachedLocations) ─
    public static bool IsStableDataValid { get; private set; }
    public static bool IsPreviewDataValid { get; private set; }

    // =========================================================================
    //  PUBLIC API — startup + path resolution
    // =========================================================================

    /// <summary>
    /// Called on app startup (and on Preview/Release toggle) to verify cached
    /// user data paths and attempt to fill them from the default AppData location
    /// if missing. Updates <see cref="IsStableDataValid"/> and
    /// <see cref="IsPreviewDataValid"/> so callers can gate features without
    /// re-checking themselves.
    /// Call this after LoadSettings() so the cached paths are already loaded.
    /// </summary>
    public static void ValidateAndUpdateCachedLocations()
    {
        Trace.WriteLine("=== [UserDataLocator] Validation Starting ===");

        IsStableDataValid = ValidateSingleEdition(isPreview: false);
        IsPreviewDataValid = ValidateSingleEdition(isPreview: true);

        Trace.WriteLine($"=== [UserDataLocator] Complete — Stable={IsStableDataValid}, Preview={IsPreviewDataValid} ===");
    }

    /// <summary>
    /// Returns the validated data root for the given edition, or null if it isn't
    /// known/valid. Callers that only care about one edition at a time (most of them)
    /// use this rather than reading the Persistent fields directly.
    /// </summary>
    public static string? GetDataRoot(bool isPreview)
    {
        var path = isPreview
            ? TunerVariables.Persistent.MinecraftPreviewDataPath
            : TunerVariables.Persistent.MinecraftDataPath;

        return IsValidDataRoot(path, isPreview) ? path : null;
    }

    /// <summary>
    /// True if the data root for the given edition is currently valid.
    /// Mirrors <see cref="IsStableDataValid"/>/<see cref="IsPreviewDataValid"/>
    /// but addressable by bool rather than two separate properties.
    /// </summary>
    public static bool IsDataValid(bool isPreview)
        => isPreview ? IsPreviewDataValid : IsStableDataValid;

    /// <summary>
    /// Attempts to accept a user-supplied path as the data root for the given edition.
    /// Validates structure, caches on success, updates the validity flag.
    /// Returns true if the path was accepted.
    /// </summary>
    public static bool TrySetCustomDataRoot(bool isPreview, string path)
    {
        if (!IsValidDataRoot(path, isPreview))
        {
            Trace.WriteLine($"[UserDataLocator] Rejected custom path (no Users subfolder): {path}");
            return false;
        }

        Trace.WriteLine($"[UserDataLocator] Accepted custom path for {(isPreview ? "Preview" : "Stable")}: {path}");
        SetCachedPath(isPreview, path);

        if (isPreview) IsPreviewDataValid = true;
        else IsStableDataValid = true;

        return true;
    }

    // ── Derived paths ---------------------------------------------------------
    // All return empty string (never null, never throw) when the root isn't valid,
    // so callers can pass the result to Directory.Exists / File.Exists without a
    // null-check dance.

    public static string GetUsersPath(bool isPreview)
    {
        var root = GetDataRoot(isPreview);
        return root is null ? string.Empty : Path.Combine(root, UsersFolderName);
    }

    public static string GetSharedComMojangPath(bool isPreview)
    {
        var users = GetUsersPath(isPreview);
        return string.IsNullOrEmpty(users) ? string.Empty
            : Path.Combine(users, SharedComMojangSubPath);
    }

    /// <summary>
    /// resource_packs or development_resource_packs under Shared\games\com.mojang.
    /// Pass createIfMissing=true for write-path callers (e.g. DeployPackage).
    /// </summary>
    public static string GetResourcePacksPath(bool isPreview, bool development = false, bool createIfMissing = false)
    {
        var comMojang = GetSharedComMojangPath(isPreview);
        if (string.IsNullOrEmpty(comMojang)) return string.Empty;

        var folder = development ? DevResourcePacksFolderName : ResourcePacksFolderName;
        var fullPath = Path.Combine(comMojang, folder);

        if (!Directory.Exists(fullPath) && createIfMissing)
        {
            try { Directory.CreateDirectory(fullPath); }
            catch { return string.Empty; }
        }

        return fullPath;
    }

    /// <summary>
    /// Both resource_packs and development_resource_packs paths that actually
    /// exist on disk. Convenient for scan-all operations (PackLocator, PackBrowser).
    /// </summary>
    public static IEnumerable<string> GetExistingResourcePackScanPaths(bool isPreview)
    {
        var rp = GetResourcePacksPath(isPreview, development: false);
        var dev = GetResourcePacksPath(isPreview, development: true);

        if (!string.IsNullOrEmpty(rp) && Directory.Exists(rp)) yield return rp;
        if (!string.IsNullOrEmpty(dev) && Directory.Exists(dev)) yield return dev;
    }

    /// <summary>
    /// All options.txt files under the Users tree (one per XUID + Shared).
    /// Returns empty array if the data root is unknown or the Users folder is absent.
    /// </summary>
    public static string[] FindAllOptionsFiles(bool isPreview)
    {
        var usersPath = GetUsersPath(isPreview);
        if (string.IsNullOrEmpty(usersPath) || !Directory.Exists(usersPath))
            return Array.Empty<string>();

        try { return Directory.GetFiles(usersPath, OptionsFileName, SearchOption.AllDirectories); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Human-readable label for the XUID or "Shared" folder that owns a given
    /// path (first segment under Users\). Used for per-file log messages.
    /// </summary>
    public static string GetOwningFolderLabel(bool isPreview, string fullPath)
    {
        var usersPath = GetUsersPath(isPreview);
        if (string.IsNullOrEmpty(usersPath))
            return Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? fullPath;

        try
        {
            var relative = Path.GetRelativePath(usersPath, fullPath);
            return relative.Split(Path.DirectorySeparatorChar)[0];
        }
        catch
        {
            return Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? fullPath;
        }
    }

    /// <summary>
    /// Display name for the targeted edition — "Minecraft" or "Minecraft Preview".
    /// </summary>
    public static string GetVersionDisplayName(bool isPreview)
        => isPreview ? "Minecraft Preview" : "Minecraft";

    // =========================================================================
    //  PRIVATE HELPERS
    // =========================================================================

    /// <summary>
    /// Validates the cached path for one edition and attempts to fill it from
    /// AppData if missing. Returns true if a valid path is now in cache.
    /// </summary>
    private static bool ValidateSingleEdition(bool isPreview)
    {
        var versionName = isPreview ? "Preview" : "Stable";
        var cachedPath = isPreview
            ? TunerVariables.Persistent.MinecraftPreviewDataPath
            : TunerVariables.Persistent.MinecraftDataPath;

        // 1. Cached path — still there and valid?
        if (!string.IsNullOrEmpty(cachedPath))
        {
            if (IsValidDataRoot(cachedPath, isPreview))
            {
                Trace.WriteLine($"[UserDataLocator] {versionName} cache valid: {cachedPath}");
                return true;
            }

            Trace.WriteLine($"[UserDataLocator] {versionName} cache invalid, clearing: {cachedPath}");
            SetCachedPath(isPreview, null);
        }

        // 2. Default AppData location
        var folderName = isPreview ? PreviewRootFolderName : StableRootFolderName;
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            folderName);

        if (IsValidDataRoot(defaultPath, isPreview))
        {
            Trace.WriteLine($"[UserDataLocator] {versionName} found at default location: {defaultPath}");
            SetCachedPath(isPreview, defaultPath);
            return true;
        }

        // 3. Not found? tell the user exactly what to look for
        Trace.WriteLine($"[UserDataLocator] {versionName} data root not found");
        return false;
    }

    /// <summary>
    /// A data root is valid if it exists on disk and contains a "Users" subfolder.
    /// This is the closest equivalent to GDKLocator's exe-presence check — the
    /// "Users" folder is created by the game on first launch and is required for
    /// all per-user data to exist under it.
    /// </summary>
    private static bool IsValidDataRoot(string? path, bool isPreview)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Directory.Exists(path)) return false;
        if (!Directory.Exists(Path.Combine(path, UsersFolderName))) return false;

        // Reject if the folder name is explicitly the wrong edition.
        // Unknown/custom names (third-party launchers) pass through unchecked.
        var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var wrongEditionName = isPreview ? StableRootFolderName : PreviewRootFolderName;
        if (folderName.Equals(wrongEditionName, StringComparison.OrdinalIgnoreCase))
        {
            Trace.WriteLine($"[UserDataLocator] Rejected path — folder name indicates wrong edition: {folderName}");
            return false;
        }

        return true;
    }

    private static void SetCachedPath(bool isPreview, string? path)
    {
        if (isPreview) TunerVariables.Persistent.MinecraftPreviewDataPath = path;
        else TunerVariables.Persistent.MinecraftDataPath = path;
    }


    /// <summary>
    /// Helper. Call at the top of any feature that depends on the current edition's user
    /// data folder. Returns true if the caller should proceed; false means the
    /// feature was short-circuited and the user has already been told what to do.
    /// Uses a live filesystem check rather than the cached validity flag, so it
    /// still catches the folder having gone missing mid-session.
    /// </summary>
    public static bool RequireValidUserData(bool isTargetingPreview)
    {
        if (GetDataRoot(isTargetingPreview) != null)
            return true;

        var versionName = GetVersionDisplayName(isTargetingPreview);
        var editionLabel = isTargetingPreview ? "Preview" : "Stable";
        var expectedFolderName = isTargetingPreview
                                 ? MinecraftUserDataLocator.PreviewRootFolderName
                                 : MinecraftUserDataLocator.StableRootFolderName;

        MainWindow.Log($"You can't use this feature without first locating where your {versionName} user data folder is located. " +
                       $"Click \"Locate {editionLabel} user data\" above, find and select the folder named \"{expectedFolderName}\" " +
                       $"- It's the one with a \"Users\" subfolder inside it.", LogLevel.Warning);

        return false;
    }
}

#endregion
