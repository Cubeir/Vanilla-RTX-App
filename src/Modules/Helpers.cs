using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Windows.Storage.Pickers;
using WinRT.Interop;

using static Vanilla_RTX_App.MainWindow;

namespace Vanilla_RTX_App.Modules;

public static class Helpers
{
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
            // Log($"Error writing direct TGA to {outputPath}: {ex.Message}");
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


    // Shortens it too
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


    public static bool IsMinecraftRunning()
    {
        var mcProcesses = Process.GetProcessesByName("Minecraft.Windows");
        return mcProcesses.Length > 0;
    }
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
        if (_flags.Contains(key))
            return false;

        _flags.Add(key);
        return true;
    }

    public static bool Unset(string key) => _flags.Remove(key);
}




// Wondering if we're being too strict with Folder names, they could be anything really, maybe third party launchers name them weirdly
// Auto location fails then right? -- the executable should be the gospel... the only light safeguard against picking the preview executable for release or vice versa is fine but
// elsewhere, it seems... counterproductive
/* https://discord.com/channels/721377277480402985/1455281964138369054/1455548708123840604
Does the app stop working if Minecraft, for whatever the reason, is named weirdly?
Should the GDKLocator's behavior be updated to: just find the game's exe?
But then, how do we differentiate preview and release?
*/
/// <summary>
/// Provides tools for locating Minecraft (Bedrock) and Minecraft Preview installations.
/// Handles caching, validation, system-wide searching, and manual selection.
///
/// Contract: every path returned or cached by this class is the PHYSICAL directory
/// containing Minecraft.Windows.exe — i.e. the Content subfolder of the install root.
/// Callers reference files as Path.Combine(installPath, "filename") directly.
/// No symlinks or junctions are ever stored — all paths are resolved to physical targets.
///
/// Location flow:
///   Phase 1 (startup, fast):
///     Cache check → Stage 0: PackageManager → Stage 1: Common locations
///   Phase 2 (async, slow):
///     System-wide recursive search across all fixed drives
///   Phase 3 (manual):
///     User picks Minecraft.Windows.exe — directory is validated and cached
/// </summary>
public static class MinecraftGDKLocator
{
    public const string MinecraftFolderName = "Minecraft for Windows";
    public const string MinecraftPreviewFolderName = "Minecraft Preview for Windows";
    public const string MinecraftExecutableName = "Minecraft.Windows.exe";
    private const int MaxSearchDepth = 9;

    // Package family names — stable post-GDK (1.21.120+)
    private const string MinecraftStablePackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    private const string MinecraftPreviewPackageFamilyName = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";

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
    /// Also detects and evicts stale symlink paths so they get re-resolved.
    /// </summary>
    public static bool RevalidateCachedPath(string? cachedPath)
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

        return true;
    }

    /// <summary>
    /// PHASE 2: Deep system-wide search for Minecraft installation.
    /// Only searches for the version the user is targeting.
    /// Can be cancelled when the user initiates manual selection.
    /// Returns the physical directory containing Minecraft.Windows.exe.
    /// </summary>
    public static async Task<string?> SearchForMinecraftAsync(bool searchForPreview, CancellationToken cancellationToken)
    {
        Trace.WriteLine($"=== PHASE 2: Deep System Search Starting (Preview={searchForPreview}) ===");

        var targetFolderName = searchForPreview ? MinecraftPreviewFolderName : MinecraftFolderName;

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

                // Priority pass: check high-probability locations on this drive first
                foreach (var priorityPath in GetCommonLocations(searchForPreview, drive))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    if (IsValidExecutableDirectory(priorityPath))
                    {
                        Trace.WriteLine($"[GDKLocator] Found at priority location: {priorityPath}");
                        CacheInstallation(searchForPreview, priorityPath);
                        return priorityPath;
                    }
                }

                // Deep recursive search of this drive
                var foundPath = await RecursiveSearchAsync(
                    drive.Name,
                    targetFolderName,
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
    /// PHASE 3: Manual selection — user picks Minecraft.Windows.exe directly.
    /// This is the clearest possible UX: no ambiguity about which folder to pick.
    /// Returns the physical directory containing the selected executable.
    /// </summary>
    public static async Task<string?> LocateMinecraftManuallyAsync(bool isPreview, IntPtr windowHandle)
    {
        Trace.WriteLine($"=== PHASE 3: Manual Selection Starting (Preview={isPreview}) ===");

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".exe");

            InitializeWithWindow.Initialize(picker, windowHandle);

            var file = await picker.PickSingleFileAsync();

            if (file == null)
            {
                Trace.WriteLine("[GDKLocator] User cancelled file selection");
                return null;
            }

            Trace.WriteLine($"[GDKLocator] User selected: {file.Path}");

            // Must be the correct executable
            if (!file.Name.Equals(MinecraftExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"[GDKLocator] Selected file is not {MinecraftExecutableName}: {file.Name}");
                return null;
            }

            var exeDirectory = Path.GetDirectoryName(file.Path);
            if (string.IsNullOrEmpty(exeDirectory))
            {
                Trace.WriteLine("[GDKLocator] Could not determine directory from selected file");
                return null;
            }

            // Resolve symlinks — user may have navigated via a junction in Explorer
            var resolvedDirectory = ResolveToPhysicalPath(exeDirectory);
            Trace.WriteLine($" [GDKLocator] Resolved exe directory: {exeDirectory} → {resolvedDirectory}");

            // Confirm the exe actually exists at the resolved path
            if (!IsValidExecutableDirectory(resolvedDirectory))
            {
                Trace.WriteLine("[GDKLocator] Resolved directory does not contain a valid Minecraft installation");
                return null;
            }

            // Verify the correct edition by checking the install root name (parent of Content)
            var unexpectedFolderName = isPreview ? MinecraftFolderName : MinecraftPreviewFolderName;
            var installRoot = Directory.GetParent(resolvedDirectory)?.Name ?? string.Empty;
            if (installRoot.Equals(unexpectedFolderName, StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"[GDKLocator] Selected wrong version — install root is: {installRoot}");
                return null;
            }

            Trace.WriteLine($"[GDKLocator] Valid installation selected: {resolvedDirectory}");
            CacheInstallation(isPreview, resolvedDirectory);
            return resolvedDirectory;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error during manual selection: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns common installation locations for a given drive (or all fixed drives).
    /// Returns the Content subdirectory directly — the directory where the exe lives.
    /// Covers Xbox App installs (XboxGames\...) and direct installs (Program Files\Microsoft Games\...).
    /// </summary>
    public static IEnumerable<string> GetCommonLocations(bool isPreview, DriveInfo? onlyDrive = null)
    {
        var folder = isPreview ? MinecraftPreviewFolderName : MinecraftFolderName;

        var drives = onlyDrive != null
            ? new[] { onlyDrive }
            : DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);

        foreach (var drive in drives)
        {
            var root = drive.RootDirectory.FullName;
            yield return Path.Combine(root, "XboxGames", folder, "Content");
            yield return Path.Combine(root, "Program Files", "Microsoft Games", folder, "Content");
        }
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Core Phase 1 logic for a single edition.
    /// Order: cache check → Stage 0 PackageManager → Stage 1 common locations.
    /// Symlink resolution is applied at every point a path is accepted.
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
            Trace.WriteLine($" [GDKLocator] Cached path: {cachedPath}");

            if (Directory.Exists(cachedPath) && IsValidExecutableDirectory(cachedPath))
            {
                // Resolve symlinks in the cached path — if the cache was written
                // before this fix existed it may hold a junction path.
                var resolved = ResolveToPhysicalPath(cachedPath);
                if (!resolved.Equals(cachedPath, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine($"  [GDKLocator] Cache was a symlink — re-caching physical path: {resolved}");
                    updateCache(resolved);
                }
                else
                {
                    Trace.WriteLine($"  [GDKLocator] Cache valid for {versionName}");
                }
                return; // Physical path is now in cache either way
            }

            Trace.WriteLine($"  [GDKLocator] Cache invalid for {versionName}, clearing");
            updateCache(null);
        }
        else
        {
            Trace.WriteLine($" [GDKLocator] No cached path for {versionName}");
        }

        // STAGE 0: PackageManager — authoritative OS query, instant
        var packagePath = TryGetInstallPathFromPackageManager(isPreview);
        if (packagePath != null)
        {
            Trace.WriteLine($"  [GDKLocator] Found {versionName} via PackageManager: {packagePath}");
            updateCache(packagePath);
            return;
        }

        // STAGE 1: Common locations across all drives
        foreach (var location in GetCommonLocations(isPreview))
        {
            Trace.WriteLine($" [GDKLocator] Checking common location: {location}");
            if (IsValidExecutableDirectory(location))
            {
                Trace.WriteLine($"  [GDKLocator] Found {versionName} at common location: {location}");
                updateCache(location);
                return;
            }
        }

        Trace.WriteLine($"  [GDKLocator] {versionName} not found in Phase 1");
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
            Trace.WriteLine($" [GDKLocator] Querying PackageManager for: {familyName}");

            var packageManager = new Windows.Management.Deployment.PackageManager();
            var packages = packageManager.FindPackagesForUser(string.Empty, familyName);

            foreach (var package in packages)
            {
                var installLocation = package.InstalledLocation?.Path;
                if (string.IsNullOrEmpty(installLocation))
                    continue;

                Trace.WriteLine($" [GDKLocator] PackageManager returned: {installLocation}");

                // Resolve the junction — this gives us the physical Content directory
                var resolvedLocation = ResolveToPhysicalPath(installLocation);
                Trace.WriteLine($" [GDKLocator] Resolved to physical path: {resolvedLocation}");

                if (IsValidExecutableDirectory(resolvedLocation))
                {
                    Trace.WriteLine($" [GDKLocator] Executable found at resolved path");
                    return resolvedLocation;
                }

                // If the junction pointed one level up (install root rather than Content),
                // step into Content and check there.
                var contentSubdir = Path.Combine(resolvedLocation, "Content");
                if (IsValidExecutableDirectory(contentSubdir))
                {
                    Trace.WriteLine($"  [GDKLocator] Executable found in Content subdir: {contentSubdir}");
                    return contentSubdir;
                }
            }

            Trace.WriteLine($" [GDKLocator] PackageManager: no valid install found for {familyName}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.WriteLine($" [GDKLocator] PackageManager access denied: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [GDKLocator] PackageManager query failed: {ex.Message}");
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
            // returnFinalTarget: true follows the full chain, not just one hop
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved))
            {
                Trace.WriteLine($" [GDKLocator] Symlink resolved: {path} → {resolved}");
                return resolved;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [GDKLocator] ResolveLinkTarget failed for {path}: {ex.Message}");
        }

        // Not a symlink or resolution failed — original path is already physical
        return path;
    }

    /// <summary>
    /// Returns true if the directory exists and directly contains Minecraft.Windows.exe.
    /// This is the canonical validity check — the contract path always satisfies this.
    /// </summary>
    private static bool IsValidExecutableDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return File.Exists(Path.Combine(path, MinecraftExecutableName));
    }

    /// <summary>
    /// Recursively searches a directory tree for the Minecraft install folder by name,
    /// then returns its Content subdirectory (where the exe lives).
    /// Used in Phase 2 only. Respects FoldersToSkip and CancellationToken.
    /// </summary>
    private static async Task<string?> RecursiveSearchAsync(
        string searchPath,
        string targetFolderName,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        if (currentDepth >= MaxSearchDepth || cancellationToken.IsCancellationRequested)
            return null;

        try
        {
            // If this folder is the install root, return its Content subdirectory
            if (Path.GetFileName(searchPath).Equals(targetFolderName, StringComparison.OrdinalIgnoreCase))
            {
                var contentPath = Path.Combine(searchPath, "Content");
                if (IsValidExecutableDirectory(contentPath))
                    return contentPath;
            }

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

                var result = await RecursiveSearchAsync(subdir, targetFolderName, currentDepth + 1, cancellationToken);
                if (result != null)
                    return result;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($" [GDKLocator] Error searching {searchPath}: {ex.Message}");
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












/// <summary>
/// Centralizes every path Minecraft's GDK build uses for user data — worlds, options,
/// resource packs, and the Shared cross-account tree. This is NOT installer-controlled;
/// these paths are dictated entirely by the GDK runtime itself and have been stable since
/// the UWP→GDK migration. Unlike install-location discovery, no searching or symlink
/// resolution is needed here — the paths are deterministic given only IsTargetingPreview.
///
/// Every consumer in the app should go through this class instead of constructing
/// "%AppData%\Minecraft Bedrock..." paths independently. If Mojang ever restructures
/// this, there is exactly one place to fix it.
/// </summary>
public static class MinecraftUserDataLocator
{
    private const string StableRootFolderName = "Minecraft Bedrock";
    private const string PreviewRootFolderName = "Minecraft Bedrock Preview";

    private const string ResourcePacksFolderName = "resource_packs";
    private const string DevResourcePacksFolderName = "development_resource_packs";
    private const string OptionsFileName = "options.txt";

    /// <summary>
    /// Result of a data-root resolution. <see cref="Exists"/> indicates whether the
    /// root folder was found at all — false means the targeted game version has
    /// never been launched (or isn't installed), and callers should fail gracefully
    /// rather than throwing or crashing.
    /// </summary>
    public readonly struct DataRoot
    {
        public bool Exists { get; init; }
        public string RootPath { get; init; }
        public string VersionDisplayName { get; init; }

        public static DataRoot Missing(string versionDisplayName) => new()
        {
            Exists = false,
            RootPath = string.Empty,
            VersionDisplayName = versionDisplayName
        };
    }

    /// <summary>
    /// Resolves the root data folder for the targeted edition
    /// (%AppData%\Minecraft Bedrock[ Preview]). Does not guarantee the folder
    /// exists — check <see cref="DataRoot.Exists"/> before using <see cref="DataRoot.RootPath"/>.
    /// </summary>
    public static DataRoot GetDataRoot(bool isTargetingPreview)
    {
        var folderName = isTargetingPreview ? PreviewRootFolderName : StableRootFolderName;
        var versionName = isTargetingPreview ? "Minecraft Preview" : "Minecraft";

        var rootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            folderName
        );

        if (!Directory.Exists(rootPath))
            return DataRoot.Missing(versionName);

        return new DataRoot
        {
            Exists = true,
            RootPath = rootPath,
            VersionDisplayName = versionName
        };
    }

    /// <summary>
    /// The per-user/shared "Users" folder, e.g. ...\Minecraft Bedrock\Users.
    /// This is where each signed-in account gets its own XUID-named subfolder,
    /// plus the special "Shared" subfolder used for packs and cross-account data.
    /// Returns empty string if the data root doesn't exist.
    /// </summary>
    public static string GetUsersPath(bool isTargetingPreview)
    {
        var root = GetDataRoot(isTargetingPreview);
        return root.Exists ? Path.Combine(root.RootPath, "Users") : string.Empty;
    }

    /// <summary>
    /// The Shared com.mojang folder: ...\Users\Shared\games\com.mojang.
    /// This is where resource packs, behavior packs, and marketplace content live —
    /// shared across every signed-in account on the machine. Returns empty string
    /// if the data root doesn't exist.
    /// </summary>
    public static string GetSharedComMojangPath(bool isTargetingPreview)
    {
        var usersPath = GetUsersPath(isTargetingPreview);
        return string.IsNullOrEmpty(usersPath)
            ? string.Empty
            : Path.Combine(usersPath, "Shared", "games", "com.mojang");
    }

    /// <summary>
    /// The Shared resource_packs folder. Created on demand if <paramref name="createIfMissing"/>
    /// is true and the parent com.mojang tree exists. Returns empty string if the data
    /// root itself doesn't exist (game never launched / not installed).
    /// </summary>
    public static string GetResourcePacksPath(bool isTargetingPreview, bool createIfMissing = false)
        => GetPacksSubfolder(isTargetingPreview, ResourcePacksFolderName, createIfMissing);

    /// <summary>
    /// The Shared development_resource_packs folder. Same semantics as <see cref="GetResourcePacksPath"/>.
    /// </summary>
    public static string GetDevelopmentResourcePacksPath(bool isTargetingPreview, bool createIfMissing = false)
        => GetPacksSubfolder(isTargetingPreview, DevResourcePacksFolderName, createIfMissing);

    /// <summary>
    /// Both resource_packs and development_resource_packs paths, only including
    /// ones that actually exist on disk. Convenient for scan-everywhere style
    /// operations like PackLocator and PackBrowser.
    /// </summary>
    public static IEnumerable<string> GetExistingResourcePackScanPaths(bool isTargetingPreview)
    {
        var resourcePacks = GetResourcePacksPath(isTargetingPreview);
        var devResourcePacks = GetDevelopmentResourcePacksPath(isTargetingPreview);

        if (!string.IsNullOrEmpty(resourcePacks) && Directory.Exists(resourcePacks))
            yield return resourcePacks;

        if (!string.IsNullOrEmpty(devResourcePacks) && Directory.Exists(devResourcePacks))
            yield return devResourcePacks;
    }

    /// <summary>
    /// Every options.txt file under the Users tree — one per signed-in account that
    /// has launched the game, plus potentially one under Shared. Returns an empty
    /// array (never null) if the data root or Users folder doesn't exist, so callers
    /// can safely foreach without a null check.
    /// </summary>
    public static string[] FindAllOptionsFiles(bool isTargetingPreview)
    {
        var usersPath = GetUsersPath(isTargetingPreview);
        if (string.IsNullOrEmpty(usersPath) || !Directory.Exists(usersPath))
            return Array.Empty<string>();

        try
        {
            return Directory.GetFiles(usersPath, OptionsFileName, SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Human-readable label for the account/Shared folder an options.txt or pack
    /// belongs to — the first path segment under Users\. Used for per-file log messages.
    /// </summary>
    public static string GetOwningFolderLabel(bool isTargetingPreview, string fullPath)
    {
        var usersPath = GetUsersPath(isTargetingPreview);
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

    // -------------------------------------------------------------------------
    // PRIVATE HELPERS
    // -------------------------------------------------------------------------

    private static string GetPacksSubfolder(bool isTargetingPreview, string subfolderName, bool createIfMissing)
    {
        var sharedComMojang = GetSharedComMojangPath(isTargetingPreview);
        if (string.IsNullOrEmpty(sharedComMojang))
            return string.Empty; // data root doesn't exist — nothing we can do

        var fullPath = Path.Combine(sharedComMojang, subfolderName);

        if (!Directory.Exists(fullPath) && createIfMissing)
        {
            try
            {
                Directory.CreateDirectory(fullPath);
            }
            catch
            {
                return string.Empty; // creation failed (permissions etc.) — caller must handle
            }
        }

        return fullPath;
    }
}
