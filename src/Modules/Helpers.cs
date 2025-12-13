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

            // Write TGA file format manually for absolute control
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
        Timeout = TimeSpan.FromSeconds(59)
    };
    static Helpers()
    {
        SharedHttpClient.DefaultRequestHeaders.Add("User-Agent", $"vanilla_rtx_app/{TunerVariables.appVersion}");
        Debug.WriteLine("✓ SharedHttpClient configured");
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
                return $"AppData\\{afterLocalState}";
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
        // Helpers for JSON formatting
        string FormatMinecraftJson(List<string> paths)
        {
            if (paths == null || paths.Count == 0)
                return "[]";
            var formattedItems = paths.Select(path => $"    \"{path}\"");
            return "[\n" + string.Join(",\n", formattedItems) + "\n]";
        }

        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"Directory not found: {rootDirectory}");
        }

        // Find all directories named "textures" recursively
        var texturesDirectories = Directory.GetDirectories(rootDirectory, "textures", SearchOption.AllDirectories)
                                          .ToList();

        // Also check if the root directory itself is named "textures"
        if (Path.GetFileName(rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("textures", StringComparison.OrdinalIgnoreCase))
        {
            texturesDirectories.Add(rootDirectory);
        }

        if (texturesDirectories.Count == 0)
        {
            return;
        }

        // Supported image types
        string[] imageExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

        foreach (string texturesDir in texturesDirectories)
        {
            // Collect all PBR texture file paths from texture sets
            var pbrTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Retrieve all PBR texture types
            var merFiles = TextureSetHelper.RetrieveFilesFromTextureSets(texturesDir, TextureSetHelper.TextureType.Mer);
            var normalFiles = TextureSetHelper.RetrieveFilesFromTextureSets(texturesDir, TextureSetHelper.TextureType.Normal);
            var heightmapFiles = TextureSetHelper.RetrieveFilesFromTextureSets(texturesDir, TextureSetHelper.TextureType.Heightmap);

            // Add all PBR textures to exclusion set
            foreach (var file in merFiles.Concat(normalFiles).Concat(heightmapFiles))
            {
                pbrTextures.Add(file);
            }

            // Collect all image files
            var imageFiles = new List<string>();
            foreach (string extension in imageExtensions)
            {
                var files = Directory.GetFiles(texturesDir, $"*{extension}", SearchOption.AllDirectories)
                                    .Concat(Directory.GetFiles(texturesDir, $"*{extension.ToUpper()}", SearchOption.AllDirectories));
                imageFiles.AddRange(files);
            }

            // Build relative paths and filter out PBR textures
            var filteredPaths = new List<string>();
            foreach (string filePath in imageFiles.Distinct())
            {
                // Skip
                if (pbrTextures.Contains(filePath))
                    continue;

                string relativePath = Path.GetRelativePath(texturesDir, filePath);
                relativePath = relativePath.Replace('\\', '/');
                string pathWithoutExtension = Path.ChangeExtension(relativePath, null);
                string finalPath = "textures/" + pathWithoutExtension;
                filteredPaths.Add(finalPath);
            }

            filteredPaths.Sort();

            string outputPath = Path.Combine(texturesDir, "textures_list.json");
            string json = FormatMinecraftJson(filteredPaths);
            File.WriteAllText(outputPath, json);

            int excludedCount = imageFiles.Distinct().Count() - filteredPaths.Count;
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





/// <summary>
/// Provides tools for locating Minecraft (Bedrock) and Minecraft Preview installations.
/// Handles caching, validation, system-wide searching, and manual selection.
/// </summary>
public static class MinecraftGDKLocator
{
    public const string MinecraftFolderName = "Minecraft for Windows";
    public const string MinecraftPreviewFolderName = "Minecraft Preview for Windows";
    private const string MinecraftExecutableName = "Minecraft.Windows.exe";
    private const int MaxSearchDepth = 9;

    // WindowsApps symlink prefixes, points to GDK Minecraft's actual location
    private static readonly string WindowsAppsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    private const string MinecraftStablePrefix = "MICROSOFT.MINECRAFTUWP_";
    private const string MinecraftPreviewPrefix = "Microsoft.MinecraftWindowsBeta_";

    private static readonly HashSet<string> FoldersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System32", "WinSxS", "$Recycle.Bin", "ProgramData",
        "AppData", "Recovery", "System Volume Information", "Config.Msi",
        "Windows.old", "PerfLogs", "Temp", "tmp"
    };

    /// <summary>
    /// PHASE 1: Quick validation of cached paths and common locations.
    /// Called on app startup. Self-contained and fast.
    /// Validates both Minecraft stable and Preview installations.
    /// </summary>
    public static void ValidateAndUpdateCachedLocations()
    {
        Debug.WriteLine("=== PHASE 1: Quick Validation Starting ===");

        // Validate Minecraft (stable)
        ValidateAndUpdateSingleInstallation(
            isPreview: false,
            cachedPath: TunerVariables.Persistent.MinecraftInstallPath,
            updateCache: (path) => TunerVariables.Persistent.MinecraftInstallPath = path
        );

        // Validate Minecraft Preview
        ValidateAndUpdateSingleInstallation(
            isPreview: true,
            cachedPath: TunerVariables.Persistent.MinecraftPreviewInstallPath,
            updateCache: (path) => TunerVariables.Persistent.MinecraftPreviewInstallPath = path
        );

        Debug.WriteLine("=== PHASE 1 Complete ===");
    }

    /// <summary>
    /// Quick re-validation of a cached path before use.
    /// Called by windows before trusting the cache.
    /// </summary>
    public static bool RevalidateCachedPath(string? cachedPath)
    {
        if (string.IsNullOrEmpty(cachedPath))
            return false;

        if (!Directory.Exists(cachedPath))
        {
            Debug.WriteLine($"⚠ Cached path no longer exists: {cachedPath}");
            return false;
        }

        if (!IsValidMinecraftPath(cachedPath))
        {
            Debug.WriteLine($"⚠ Cached path no longer valid: {cachedPath}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// PHASE 2: Deep system-wide search for Minecraft installation.
    /// Only searches for the version the user is targeting.
    /// Can be cancelled by user initiating manual selection.
    /// </summary>
    public static async Task<string?> SearchForMinecraftAsync(bool searchForPreview, CancellationToken cancellationToken)
    {
        Debug.WriteLine($"=== PHASE 2: Deep System Search Starting (Preview={searchForPreview}) ===");

        var targetFolderName = searchForPreview ? MinecraftPreviewFolderName : MinecraftFolderName;

        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();

            Debug.WriteLine($"Found {drives.Count} fixed drives to search");

            foreach (var drive in drives)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Debug.WriteLine("✗ Search cancelled by user");
                    return null;
                }

                Debug.WriteLine($"Scanning drive: {drive.Name}");

                // Priority search: Check high-probability locations first
                var priorityPaths = new[]
                {
                    Path.Combine(drive.Name, "XboxGames", targetFolderName),
                    Path.Combine(drive.Name, "Program Files", "Microsoft Games", targetFolderName)
                };

                foreach (var priorityPath in priorityPaths)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;

                    if (IsValidMinecraftPath(priorityPath))
                    {
                        Debug.WriteLine($"✓ Found target at priority location: {priorityPath}");
                        CacheInstallation(searchForPreview, priorityPath);
                        return priorityPath;
                    }
                }

                // Deep recursive search
                var foundPath = await RecursiveSearchAsync(
                    drive.Name,
                    targetFolderName,
                    currentDepth: 0,
                    cancellationToken
                );

                if (foundPath != null)
                {
                    Debug.WriteLine($"✓ Found target via deep search: {foundPath}");
                    CacheInstallation(searchForPreview, foundPath);
                    return foundPath;
                }
            }

            Debug.WriteLine("✗ Target not found on any drive");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Error during system search: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// PHASE 3: Manual folder selection with validation.
    /// User must select the correct Minecraft root folder (where Content/Minecraft.Windows.exe exists).
    /// </summary>
    public static async Task<string?> LocateMinecraftManuallyAsync(bool isPreview, IntPtr windowHandle)
    {
        Debug.WriteLine($"=== PHASE 3: Manual Selection Starting (Preview={isPreview}) ===");

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
                Debug.WriteLine("✗ User cancelled folder selection");
                return null;
            }

            Debug.WriteLine($"User selected: {folder.Path}");

            var expectedFolderName = isPreview ? MinecraftPreviewFolderName : MinecraftFolderName;
            var unexpectedFolderName = isPreview ? MinecraftFolderName : MinecraftPreviewFolderName;
            var selectedFolderName = Path.GetFileName(folder.Path);

            // Check if user selected the wrong version
            if (selectedFolderName.Equals(unexpectedFolderName, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"✗ User selected wrong version: {selectedFolderName}");
                return null;
            }

            string pathToValidate = folder.Path;

            // Handle if user selected the "Content" subfolder instead of root
            if (selectedFolderName.Equals("Content", StringComparison.OrdinalIgnoreCase))
            {
                var parentPath = Directory.GetParent(folder.Path)?.FullName;
                if (parentPath != null)
                {
                    Debug.WriteLine("User selected Content folder, checking parent");
                    pathToValidate = parentPath;
                }
            }

            // Validate the path
            if (IsValidMinecraftPath(pathToValidate))
            {
                var finalFolderName = Path.GetFileName(pathToValidate);
                var isUnexpectedVersion = finalFolderName.Equals(unexpectedFolderName, StringComparison.OrdinalIgnoreCase);

                if (isUnexpectedVersion)
                {
                    Debug.WriteLine($"✗ Path valid but wrong version: {finalFolderName}");
                    return null;
                }

                Debug.WriteLine($"✓ Valid installation selected: {pathToValidate}");
                CacheInstallation(isPreview, pathToValidate);
                return pathToValidate;
            }

            Debug.WriteLine($"✗ Selected folder is not valid Minecraft installation");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Error during manual selection: {ex.Message}");
            return null;
        }
    }

    #region Private Helper Methods

    /// <summary>
    /// Validates a single installation (stable or preview) and updates cache.
    /// Follows search priority: cached path -> common locations -> WindowsApps symlinks.
    /// </summary>
    private static void ValidateAndUpdateSingleInstallation(
        bool isPreview,
        string? cachedPath,
        Action<string?> updateCache)
    {
        var versionName = isPreview ? "Preview" : "Stable";
        Debug.WriteLine($"Validating {versionName} Minecraft...");

        // Check cached path first
        if (!string.IsNullOrEmpty(cachedPath))
        {
            Debug.WriteLine($"  Cached path: {cachedPath}");

            if (Directory.Exists(cachedPath) && IsValidMinecraftPath(cachedPath))
            {
                Debug.WriteLine($"  ✓ Cache valid for {versionName}");
                return;
            }

            Debug.WriteLine($"  ✗ Cache invalid for {versionName}, clearing");
            updateCache(null);
        }
        else
        {
            Debug.WriteLine($"  No cached path for {versionName}");
        }

        // STAGE 1: Try common locations (highest success rate)
        var commonLocations = GetCommonLocations(isPreview);
        foreach (var location in commonLocations)
        {
            Debug.WriteLine($"  Checking common location: {location}");

            if (Directory.Exists(location) && IsValidMinecraftPath(location))
            {
                Debug.WriteLine($"  ✓ Found {versionName} at common location: {location}");
                updateCache(location);
                return;
            }
        }

        // STAGE 2: Try WindowsApps symlinks as last resort (may fail due to permissions)
        Debug.WriteLine($"  Common locations failed, trying WindowsApps symlinks...");
        var symlinkPath = TryResolveWindowsAppsSymlink(isPreview);
        if (symlinkPath != null)
        {
            Debug.WriteLine($"  ✓ Found {versionName} via WindowsApps symlink: {symlinkPath}");
            updateCache(symlinkPath);
            return;
        }

        Debug.WriteLine($"  ✗ {versionName} not found in Phase 1");
    }

    /// <summary>
    /// STAGE 2: Resolve WindowsApps symlinks to get actual installation path.
    /// This is a last resort as the folder requires elevated permissions.
    /// </summary>
    private static string? TryResolveWindowsAppsSymlink(bool isPreview)
    {
        try
        {
            if (!Directory.Exists(WindowsAppsPath))
            {
                Debug.WriteLine($"  WindowsApps directory not found: {WindowsAppsPath}");
                return null;
            }

            var prefix = isPreview ? MinecraftPreviewPrefix : MinecraftStablePrefix;
            Debug.WriteLine($"  Searching WindowsApps for: {prefix}*");

            var directories = Directory.GetDirectories(WindowsAppsPath, $"{prefix}*");

            foreach (var dir in directories)
            {
                Debug.WriteLine($"  Found WindowsApps entry: {dir}");

                try
                {
                    // Check if it's a symlink/junction
                    var dirInfo = new DirectoryInfo(dir);
                    var linkTarget = dirInfo.LinkTarget;

                    if (!string.IsNullOrEmpty(linkTarget))
                    {
                        Debug.WriteLine($"  Symlink target: {linkTarget}");

                        if (IsValidMinecraftPath(linkTarget))
                        {
                            Debug.WriteLine($"  ✓ Symlink resolves to valid Minecraft installation");
                            return linkTarget;
                        }
                    }
                    else
                    {
                        // Not a symlink, might be actual install location
                        if (IsValidMinecraftPath(dir))
                        {
                            Debug.WriteLine($"  ✓ WindowsApps entry is valid installation");
                            return dir;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"  Error resolving symlink {dir}: {ex.Message}");
                }
            }

            Debug.WriteLine($"  ✗ No valid WindowsApps entry found for {prefix}");
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Debug.WriteLine($"  ✗ Access denied to WindowsApps (expected on most systems)");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"  ✗ Error accessing WindowsApps: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Validates if a path contains a valid Minecraft installation.
    /// Checks for Content/Minecraft.Windows.exe
    /// </summary>
    private static bool IsValidMinecraftPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return FindMinecraftExecutable(path) != null;
    }
    /// <summary>
    /// Recursively searches for Minecraft.Windows.exe (case-insensitive).
    /// Searches top-level folders first for speed.
    /// </summary>
    private static string? FindMinecraftExecutable(string rootPath, int maxDepth = 3)
    {
        try
        {
            // Check root first
            var files = Directory.GetFiles(rootPath, MinecraftExecutableName, SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
                return files[0];

            // BFS approach: check all immediate subdirectories first (Content folder priority)
            var immediateSubdirs = Directory.GetDirectories(rootPath);
            foreach (var subdir in immediateSubdirs)
            {
                var subdirName = Path.GetFileName(subdir);
                if (subdirName.Equals("Content", StringComparison.OrdinalIgnoreCase))
                {
                    files = Directory.GetFiles(subdir, MinecraftExecutableName, SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                        return files[0];
                }
            }

            // Then check other immediate subdirectories
            foreach (var subdir in immediateSubdirs)
            {
                var subdirName = Path.GetFileName(subdir);
                if (!subdirName.Equals("Content", StringComparison.OrdinalIgnoreCase))
                {
                    files = Directory.GetFiles(subdir, MinecraftExecutableName, SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                        return files[0];
                }
            }

            // Finally, deeper recursive search (limited depth)
            return RecursiveFindExecutable(rootPath, 0, maxDepth);
        }
        catch
        {
            return null;
        }
    }

    private static string? RecursiveFindExecutable(string path, int currentDepth, int maxDepth)
    {
        if (currentDepth >= maxDepth)
            return null;

        try
        {
            var subdirs = Directory.GetDirectories(path);
            foreach (var subdir in subdirs)
            {
                var files = Directory.GetFiles(subdir, MinecraftExecutableName, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                    return files[0];

                var found = RecursiveFindExecutable(subdir, currentDepth + 1, maxDepth);
                if (found != null)
                    return found;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Returns array of common installation locations.
    /// STAGE 1 search targets - highest probability locations.
    /// </summary>
    public static string[] GetCommonLocations(bool isPreview)
    {
        var folder = isPreview ? MinecraftPreviewFolderName : MinecraftFolderName;
        var programFilesRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ?? "C:\\";

        var list = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(d => Path.Combine(d.RootDirectory.FullName, "XboxGames", folder))
            .ToList();

        list.Add(Path.Combine(programFilesRoot, "Program Files", "Microsoft Games", folder));
        return list.ToArray();
    }


    /// <summary>
    /// Recursively searches a directory tree for Minecraft installation.
    /// Limited to MaxSearchDepth levels to prevent excessive scanning.
    /// Can be cancelled via CancellationToken.
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
            var currentFolderName = Path.GetFileName(searchPath);
            if (currentFolderName.Equals(targetFolderName, StringComparison.OrdinalIgnoreCase))
            {
                if (IsValidMinecraftPath(searchPath))
                    return searchPath;
            }

            var subdirectories = await Task.Run(() =>
            {
                try
                {
                    return Directory.GetDirectories(searchPath);
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }, cancellationToken);

            foreach (var subdir in subdirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                var subdirName = Path.GetFileName(subdir);
                if (FoldersToSkip.Contains(subdirName))
                    continue;

                var result = await RecursiveSearchAsync(subdir, targetFolderName, currentDepth + 1, cancellationToken);
                if (result != null)
                    return result;
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error searching {searchPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Caches a discovered installation path to persistent storage.
    /// Stores as string for serialization compatibility.
    /// </summary>
    private static void CacheInstallation(bool isPreview, string path)
    {
        if (isPreview)
        {
            TunerVariables.Persistent.MinecraftPreviewInstallPath = path;
            Debug.WriteLine($"✓ Cached Preview installation: {path}");
        }
        else
        {
            TunerVariables.Persistent.MinecraftInstallPath = path;
            Debug.WriteLine($"✓ Cached Stable installation: {path}");
        }
    }

    #endregion
}
