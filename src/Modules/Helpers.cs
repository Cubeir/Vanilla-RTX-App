using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ImageMagick;
using Newtonsoft.Json.Linq;
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

            // this loop used to call sourcePixels.GetPixel(x, y) - a native
            // ImageMagick call per pixel - AND bitmap.SetPixel(x, y, ...) - a native
            // GDI+ call per pixel. Both were replaced with a single bulk native
            // call on each side: GetValues() fetches every channel of every pixel in
            // one call (verified against Magick.NET's own test suite: the returned
            // array's length is exactly width * height * channelCount, in the same
            // row-major, channel-interleaved order that indexing a single GetPixel(x,y)
            // result already used), and FastBitmap replaces the GDI+ side. The decode
            // logic itself - every ColorType branch, the >>8 truncation, maxOpacity -
            // is untouched; only how the raw channel values are fetched changed.
            using (var sourcePixels = sourceImage.GetPixels())
            using (var fb = new FastBitmap(bitmap, writable: true))
            {
                var channelCount = (int)sourceImage.ChannelCount;
                var values = sourcePixels.GetValues()
                    ?? throw new InvalidOperationException($"Failed to read pixel data for '{imagePath}'.");

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var pixelIndex = (y * width + x) * channelCount;

                        byte r, g, b, a;

                        var hasAlpha = sourceImage.HasAlpha || sourceImage.ColorType == ColorType.GrayscaleAlpha || sourceImage.ColorType == ColorType.TrueColorAlpha;

                        if (sourceImage.ColorType == ColorType.Grayscale)
                        {
                            var gray = (byte)(values[pixelIndex + 0] >> 8);
                            r = g = b = gray;
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.GrayscaleAlpha)
                        {
                            var gray = (byte)(values[pixelIndex + 0] >> 8);
                            r = g = b = gray;
                            var originalAlpha = (byte)(values[pixelIndex + 1] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColor)
                        {
                            r = (byte)(values[pixelIndex + 0] >> 8);
                            g = (byte)(values[pixelIndex + 1] >> 8);
                            b = (byte)(values[pixelIndex + 2] >> 8);
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColorAlpha)
                        {
                            r = (byte)(values[pixelIndex + 0] >> 8);
                            g = (byte)(values[pixelIndex + 1] >> 8);
                            b = (byte)(values[pixelIndex + 2] >> 8);
                            var originalAlpha = (byte)(values[pixelIndex + 3] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.Palette)
                        {
                            r = (byte)(values[pixelIndex + 0] >> 8);
                            g = (byte)(values[pixelIndex + 1] >> 8);
                            b = (byte)(values[pixelIndex + 2] >> 8);

                            if (hasAlpha && sourceImage.ChannelCount > 3)
                            {
                                var originalAlpha = (byte)(values[pixelIndex + 3] >> 8);
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

                            r = channels > 0 ? (byte)(values[pixelIndex + 0] >> 8) : (byte)0;
                            g = channels > 1 ? (byte)(values[pixelIndex + 1] >> 8) : r;
                            b = channels > 2 ? (byte)(values[pixelIndex + 2] >> 8) : r;

                            if (hasAlpha && channels > 3)
                            {
                                var originalAlpha = (byte)(values[pixelIndex + 3] >> 8);
                                a = maxOpacity ? (byte)255 : originalAlpha;
                            }
                            else
                            {
                                a = 255;
                            }
                        }

                        fb[x, y] = Color.FromArgb(a, r, g, b);
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

            // FIX: was bitmap.GetPixel(x, y) per pixel - every TGA save (the majority
            // format for RTX PBR packs) paid this. FastBitmap bulk-copies the whole
            // buffer once via LockBits/Marshal.Copy, then reads are plain array indexing.
            using var fb = new FastBitmap(bitmap, writable: false);

            for (var y = height - 1; y >= 0; y--) // TGA is bottom-up by default
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = fb[x, y];

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


    #region NETWORK

    public static readonly HttpClient SharedHttpClient = CreateClient();
    public static readonly HttpClient UpdaterHttpClient = CreateClient("updater");

    static Helpers()
    {
        Trace.WriteLine("[HttpsHelper] SharedHttpClient and UpdaterHttpClient configured");
    }

    private static HttpClient CreateClient(string? component = null)
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("User-Agent", BuildUserAgent(component));
        return client;
    }

    /// <summary>
    /// Builds the app's User-Agent string. Pass a component name (e.g. "updater")
    /// to tag requests from a specific feature; omit it for the default app-wide UA.
    /// </summary>
    public static string BuildUserAgent(string? component = null) =>
        component is null
            ? $"vanilla_rtx_app/{TunerVariables.appVersion}"
            : $"vanilla_rtx_app_{component}/{TunerVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)";
    /// <summary>
    /// Downloads a file with progress tracking and retry logic.
    /// Uses the shared HttpClient which is pre-configured.
    /// For custom timeout/headers, pass a custom HttpClient.
    /// Pass quiet: true to keep progress out of the UI log and send it to Trace instead.
    /// </summary>
    public static async Task<(bool, string?)> Download(
            string url,
            CancellationToken cancellationToken = default,
            HttpClient? httpClient = null,
            TimeSpan? timeout = null,
            bool quiet = false)
    {
        // Background callers (AssetUpdater) download things the user never asked for and
        // shouldn't have to read about; everything else keeps reporting to the UI log.
        void Report(string message, LogLevel level)
        {
            if (quiet) Trace.WriteLine($"[Download] {message}");
            else Log(message, level);
        }

        var client = httpClient ?? SharedHttpClient;
        var retries = 3;

        while (retries-- > 0)
        {
            using var timeoutCts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            timeoutCts?.CancelAfter(timeout!.Value);
            var token = timeoutCts?.Token ?? cancellationToken;

            try
            {
                // === DOWNLOAD ===
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                Report("Starting Download.", LogLevel.Lengthy);

                var totalBytes = response.Content.Headers.ContentLength;
                if (!totalBytes.HasValue)
                    Report("Total file size unknown. Progress will be logged as total downloaded (in MegaBytes).", LogLevel.Informational);

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
                        Report($"No valid filename found, using random name: {fileName}", LogLevel.Informational);
                    }
                    else
                    {
                        Report("File name: " + fileName, LogLevel.Informational);
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
                    Report($"Save location: {savingLocation}", LogLevel.Cache);
                }
                catch (Exception ex)
                {
                    Report($"Failed to establish save location: {ex.Message}", LogLevel.Error);
                    savingLocation = null;
                }

                if (savingLocation == null)
                {
                    Report("No writable location found for download.", LogLevel.Error);
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

                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                    totalRead += read;

                    if (totalBytes.HasValue)
                    {
                        var progress = (double)totalRead / totalBytes.Value * 100;
                        if (progress - lastLoggedProgress >= 10 || progress >= 100)
                        {
                            lastLoggedProgress = progress;
                            Report($"Download Progress: {progress:0}%", LogLevel.Informational);
                        }
                    }
                    else
                    {
                        var currentMB = (int)(totalRead / (1024 * 1024));
                        if (currentMB > lastLoggedMB)
                        {
                            lastLoggedMB = currentMB;
                            Report($"Download Progress: {currentMB} MB", LogLevel.Informational);
                        }
                    }
                }

                Report("Download finished successfully.", LogLevel.Success);
                return (true, savingLocation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Report("Download cancelled by the caller.", LogLevel.Informational);
                return (false, null);
            }
            catch (HttpRequestException ex) when (retries > 0)
            {
                Report($"Transient error: {ex.Message}. Retrying...", LogLevel.Warning);
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException) when (retries > 0)
            {
                Report("Request timed out. Retrying...", LogLevel.Warning);
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Report("Request timed out after all retries.", LogLevel.Error);
                return (false, null);
            }
            catch (Exception ex)
            {
                Report($"Error during download: {ex.Message}", LogLevel.Error);
                return (false, null);
            }
        }

        Report("Download failed after multiple attempts.", LogLevel.Error);
        return (false, null);
    }

    #endregion NETWORK

    // TODO: Re-examine the approach, could it be improved? or is it reliable enough?
    // maybe not saving a file and directly launching the command? -- AVs are the most concerning thing about this, possibly?
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
                Trace.WriteLine($"{logPrefix} ReplaceFilesWithElevation called with no files - nothing to do");
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
    /// A filesystem helper that recursively finds files matching <paramref name="searchPattern"/> whose directory
    /// depth relative to <paramref name="rootDirectory"/> falls within [minDepth, maxDepth].
    /// Depth 0 = rootDirectory itself, depth 1 = its immediate subfolders, etc.
    /// Stops descending once maxDepth is reached (won't walk deeper subtrees unnecessarily).
    /// Silently skips directories it can't access.
    /// </summary>
    public static IEnumerable<string> FindFilesAtDepth(
        string rootDirectory, string searchPattern, int minDepth, int maxDepth)
    {
        if (minDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(minDepth));
        if (maxDepth < minDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth));

        return Traverse(rootDirectory, 0);

        IEnumerable<string> Traverse(string dir, int depth)
        {
            if (depth >= minDepth)
            {
                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(dir, searchPattern); }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }

                foreach (var f in files)
                    yield return f;
            }

            if (depth < maxDepth)
            {
                string[] subdirs = Array.Empty<string>();
                try { subdirs = Directory.GetDirectories(dir); }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }

                foreach (var sub in subdirs)
                    foreach (var f in Traverse(sub, depth + 1))
                        yield return f;
            }
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

            // Distinct AFTER stripping extensions, not just on file paths: the same texture
            // name can legitimately exist under two extensions, and the game resolves that
            // to one texture (.tga > .png > .jpg > .jpeg). Alchitex creates exactly this
            // situation when it rewrites a colour texture as .tga next to the original -
            // without this the list carries the same entry twice.
            filteredPaths = filteredPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            filteredPaths.Sort();

            File.WriteAllText(
                Path.Combine(texturesDir, "textures_list.json"),
                FormatMinecraftJson(filteredPaths));
        }
    }


    /// <summary>
    /// Every file the game writes into an installed pack for its own use - caches and
    /// signatures, never authored content. Both spellings of the textures list (and of the
    /// signature file) are here because packs in the wild carry either.
    /// </summary>
    private static readonly string[] BookkeepingFileNames =
    {
        "contents.json", "textures_list.json", "texture_list.json", "signatures.json", "signature.json",
    };

    /// <summary>
    /// Strips every game-generated bookkeeping file out of a pack, recursively.
    ///
    /// Used when a pack leaves the app (export): the game treats these as authoritative
    /// caches, so shipping a stale one is strictly worse than shipping none - anything it
    /// fails to list simply doesn't load. Also the first half of RegenerateBookkeepingFiles,
    /// since the singular "texture_list.json" and the signature files are never regenerated
    /// and so have to be deleted rather than overwritten.
    ///
    /// contents.json is written read-only by the game, hence the attribute clearing. A file
    /// that can't be deleted (locked by the game, AV, indexing) is logged and skipped rather
    /// than aborting the sweep.
    /// </summary>
    public static void RemoveBookkeepingFiles(string packRoot)
    {
        if (!Directory.Exists(packRoot)) return;

        foreach (var name in BookkeepingFileNames)
        {
            string[] matches;
            try { matches = Directory.GetFiles(packRoot, name, SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Bookkeeping] Couldn't scan '{packRoot}' for '{name}': {ex.Message}");
                continue;
            }

            foreach (var file in matches)
                TryDeleteBookkeepingFile(file);
        }
    }

    /// <summary>
    /// Writes the bookkeeping files an *installed* pack is expected to have: a
    /// textures_list.json in every textures folder (GenerateTexturesLists) plus an empty
    /// contents.json next to manifest.json.
    ///
    /// Only ever for packs this app produced or deployed itself - Vanilla RTX installs
    /// (PackUpdater) and Alchitex's generated RTX packs, where regenerating caches is part
    /// of the job we were asked to do. Deliberately NOT done on plain imports: an imported
    /// pack is somebody else's work, and rewriting its bookkeeping would be mutilating it
    /// rather than importing it.
    ///
    /// contents.json is the game's own file-location cache and can't be authored by us; an
    /// empty object is enough for the game to rebuild it, and is what keeps it from trusting
    /// whatever stale one was there before.
    ///
    /// Each half is independently guarded - a failure to list textures shouldn't cost the
    /// pack its contents.json, and neither is worth failing a whole install/generation over.
    /// </summary>
    public static void GenerateBookkeepingFiles(string packRoot)
    {
        try
        {
            GenerateTexturesLists(packRoot);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Bookkeeping] textures_list.json generation failed for '{packRoot}': {ex.Message}");
        }

        var contentsPath = Path.Combine(packRoot, "contents.json");
        try
        {
            if (File.Exists(contentsPath)) TryDeleteBookkeepingFile(contentsPath);
            File.WriteAllText(contentsPath, "{}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Bookkeeping] Couldn't write '{contentsPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Clears out every stale bookkeeping file and then writes fresh ones. For packs whose
    /// contents we just changed on disk (Alchitex): overwriting isn't enough on its own,
    /// because the file names we no longer generate would otherwise survive and keep
    /// pointing at a pack that no longer looks like that.
    /// </summary>
    public static void RegenerateBookkeepingFiles(string packRoot)
    {
        RemoveBookkeepingFiles(packRoot);
        GenerateBookkeepingFiles(packRoot);
    }

    private static void TryDeleteBookkeepingFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & System.IO.FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~System.IO.FileAttributes.ReadOnly);

            File.Delete(path);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Bookkeeping] Couldn't delete '{path}': {ex.Message}");
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
/// containing Minecraft.Windows.exe - i.e. the Content subfolder of the install root.
/// Callers reference files as Path.Combine(installPath, "filename") directly.
/// No symlinks or junctions are ever stored - all paths are resolved to physical targets.
///
/// Edition detection (Preview vs Stable) is authoritative, not name-based: every
/// GDK Minecraft install ships a MicrosoftGame.Config next to the exe whose
/// <Identity Name="..."/> attribute is "Microsoft.MinecraftUWP" (stable) or
/// "Microsoft.MinecraftWindowsBeta" (preview). This value is baked in by Mojang/Microsoft
/// and is independent of folder names, GUIDs, drive letters, or which launcher installed it.
/// Folder names and known package GUIDs are used only as fast-path optimizations to try
/// first - they are never required for correctness.
///
/// Location flow:
///   Phase 1 (startup, fast):
///     Cache check → Stage 0: PackageManager → Stage 1: Common locations
///   Phase 2 (async, slow):
///     System-wide recursive search across all fixed drives - matches on the
///     presence of Minecraft.Windows.exe + a MicrosoftGame.Config with the
///     correct Identity, never on folder name.
///   Phase 3 (manual):
///     User picks Minecraft.Windows.exe - directory is validated and cached
/// </summary>
public static class MinecraftGDKLocator
{
    public const string MinecraftFolderName = "Minecraft for Windows";
    public const string MinecraftPreviewFolderName = "Minecraft Preview for Windows";
    public const string MinecraftExecutableName = "Minecraft.Windows.exe";
    private const string GameConfigFileName = "MicrosoftGame.Config";
    private const int MaxSearchDepth = 9;

    // Package family names - stable post-GDK (1.21.120+)
    private const string MinecraftStablePackageFamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
    private const string MinecraftPreviewPackageFamilyName = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";

    // MicrosoftGame.Config <Identity Name="..."/> values - the authoritative,
    // folder-name-independent way to tell stable and preview apart. These are
    // the same identity strings the package family names above are built from,
    // and they have remained unchanged even through the "Beta" → "Preview" rebrand.
    private const string MinecraftStableIdentityName = "Microsoft.MinecraftUWP";
    private const string MinecraftPreviewIdentityName = "Microsoft.MinecraftWindowsBeta";

    // Known Microsoft Store install GUIDs used in place of friendly folder names
    // by some install paths. Treated as fully interchangeable with the friendly
    // names below - both are just fast-path hints, never a requirement.
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

        // Evict if the cached path is still a symlink - force re-discovery
        // so the physical path gets written to cache instead.
        var resolved = ResolveToPhysicalPath(cachedPath);
        if (!resolved.Equals(cachedPath, StringComparison.OrdinalIgnoreCase))
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path is a symlink - evicting so physical path gets cached: {resolved}");
            return false;
        }

        // Authoritative edition check via MicrosoftGame.Config. If the config is
        // missing or unreadable we don't evict on that basis alone (degrade gracefully -
        // see TryGetEditionFromGameConfig), but a confirmed mismatch is disqualifying.
        var detectedEdition = TryGetEditionFromGameConfig(resolved);
        if (detectedEdition.HasValue && detectedEdition.Value != expectedPreview)
        {
            Trace.WriteLine($"[GDKLocator] ⚠ Cached path edition mismatch (expected Preview={expectedPreview}, found Preview={detectedEdition.Value}) - evicting");
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
    /// Identity), never on folder name - friendly names and known GUIDs are only
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

                // Deep recursive search of this drive - matches on exe + config identity only,
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
    /// PHASE 3: Manual selection - user picks a folder near the installation
    /// (folder picker, not file picker: the exe itself may sit in a
    /// permission-protected directory that the OS won't allow the app to "open,"
    /// even though only the path is needed - folders don't carry that restriction).
    ///
    /// Tolerant of imprecision: accepts the exact folder containing Minecraft.Windows.exe,
    /// or a folder one level shallower (the install root, whose child folder - named
    /// anything, including a GUID - holds the exe). This mirrors the leniency
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
                    Trace.WriteLine($"[GDKLocator] Selected wrong version - MicrosoftGame.Config identifies this as {foundName}, expected {expectedName}");
                    return null;
                }
            }
            else
            {
                // No usable config - soft folder-name guard as last resort, same as before.
                var unexpectedFolderName = isPreview ? MinecraftFolderName : MinecraftPreviewFolderName;
                var installRoot = Directory.GetParent(exeDirectory)?.Name ?? string.Empty;
                if (installRoot.Equals(unexpectedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine($"[GDKLocator] Selected wrong version - install root is: {installRoot}");
                    return null;
                }
                Trace.WriteLine("[GDKLocator] MicrosoftGame.Config unavailable - proceeding on unverified edition (folder name didn't indicate a mismatch)");
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
    /// level deeper - tolerating the user having selected the install root instead
    /// of the exe's own folder. No name assumption on that child folder: it could be
    /// "Content", a GUID, or anything a third-party launcher decided to call it.
    /// Each candidate is symlink-resolved before being checked, since a subfolder
    /// can itself turn out to be a junction. This is a bounded, one-hop convenience -
    /// not a search; Phase 2 already owns unbounded discovery.
    /// </summary>
    private static string? FindExecutableDirectoryNearby(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
            return null;

        // Direct hit - selected folder already contains the exe
        if (IsValidExecutableDirectory(selectedPath))
            return selectedPath;

        // One level deeper - selected folder was probably the install root
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
    /// Includes both friendly folder names and known Microsoft Store GUIDs - the two
    /// are fully interchangeable as far as this locator is concerned, since some
    /// installers use one and some use the other. Returns the Content subdirectory
    /// directly - the directory where the exe lives.
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
            // Direct Microsoft Store install, GUID-named - fully equivalent to the friendly name
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
            // pointing at an otherwise-correct physical path - try that quick
            // resolve-and-recache before falling all the way through to Stage 0/1.
            if (Directory.Exists(cachedPath) && IsValidExecutableDirectory(cachedPath))
            {
                var resolved = ResolveToPhysicalPath(cachedPath);
                var resolvedEdition = TryGetEditionFromGameConfig(resolved);
                if (!resolvedEdition.HasValue || resolvedEdition.Value == isPreview)
                {
                    Trace.WriteLine($"[GDKLocator] Cache was a symlink - re-caching physical path: {resolved}");
                    updateCache(resolved);
                    return;
                }
            }
        }
        else
        {
            Trace.WriteLine($"[GDKLocator] No cached path for {versionName}");
        }

        // STAGE 0: PackageManager - authoritative OS query, instant
        var packagePath = TryGetInstallPathFromPackageManager(isPreview);
        if (packagePath != null)
        {
            Trace.WriteLine($"[GDKLocator] Found {versionName} via PackageManager: {packagePath}");
            updateCache(packagePath);
            return;
        }
        // STAGE 0.5 (0's FALLBACK): Try to look up the junction has a hardcoded path, a hail mary in case previous step fails, before moving on
        var junctionPath = TryGetInstallPathFromWindowsAppsJunction(isPreview);
        if (junctionPath != null)
        {
            Trace.WriteLine($"[GDKLocator] Found {versionName} via a blind try at hardcoded Junction/Symlink resolution: {junctionPath}");
            updateCache(junctionPath);
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
    /// PackageManager returns the WindowsApps junction - resolved to the physical
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
    /// STAGE 0.5: Direct WindowsApps junction lookup — a cheap, single-directory
    /// fallback for the rare case PackageManager itself fails (policy restrictions,
    /// odd app contexts) despite the package actually being installed. The junction's
    /// naming convention (family name + version + architecture) is stable and
    /// well-documented, unlike a full-drive search.
    /// </summary>
    private static string? TryGetInstallPathFromWindowsAppsJunction(bool isPreview)
    {
        var familyName = isPreview ? MinecraftPreviewPackageFamilyName : MinecraftStablePackageFamilyName;
        var windowsAppsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");

        if (!Directory.Exists(windowsAppsPath))
            return null;

        try
        {
            // Junction folders are named "{PackageFamilyName-prefix}_{version}_{arch}__{publisherId}"
            // e.g. Microsoft.MinecraftUWP_1.26.3005.0_x64__8wekyb3d8bbwe
            var familyPrefix = familyName.Split('_')[0]; // "Microsoft.MinecraftUWP"

            foreach (var dir in Directory.GetDirectories(windowsAppsPath, $"{familyPrefix}_*"))
            {
                var resolved = ResolveToPhysicalPath(dir);

                if (IsValidExecutableDirectoryForEdition(resolved, isPreview))
                    return resolved;

                var contentSubdir = Path.Combine(resolved, "Content");
                if (IsValidExecutableDirectoryForEdition(contentSubdir, isPreview))
                    return contentSubdir;
            }
        }
        catch (UnauthorizedAccessException)
        {
            Trace.WriteLine("[GDKLocator] Access denied to WindowsApps folder");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[GDKLocator] Error scanning WindowsApps: {ex.Message}");
        }

        return null;
    }
    /// <summary>
    /// Resolves a path to its physical target, following symlinks/junctions to the end.
    /// Returns the original path unchanged if it is not a link or resolution fails.
    /// Safe to call on any path - non-links are a no-op.
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
    /// This is the canonical validity check - the contract path always satisfies this.
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
    /// is missing or unparseable, this degrades to exe-presence only - we never
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

            // Recognized config, but an identity we don't know - don't guess.
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
    /// edition. Matching is based entirely on directory contents - Minecraft.Windows.exe
    /// plus a MicrosoftGame.Config confirming the edition - never on folder name. This
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
            // Test this directory directly - exe presence + confirmed edition.
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
/// Centralizes discovery and validation of Minecraft's GDK user data root -
/// the folder that contains worlds, options, resource packs, and the Shared tree.
///
/// Contract: the path stored in TunerVariables.Persistent.MinecraftDataPath (and
/// MinecraftPreviewDataPath) is always the "Minecraft Bedrock" or "Minecraft Bedrock
/// Preview" root folder - the one that directly contains a "Users" subfolder.
/// All deeper paths (com.mojang, resource_packs, options.txt) are derived from this
/// root on demand via the helper methods below.
///
/// Unlike GDKLocator, there is no exe or config file to serve as an absolute gospel
/// here - validation is based on folder structure (presence of the "Users" subfolder).
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
    //  PUBLIC API - startup + path resolution
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

        Trace.WriteLine($"=== [UserDataLocator] Complete - Stable={IsStableDataValid}, Preview={IsPreviewDataValid} ===");
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
    /// Display name for the targeted edition - "Minecraft" or "Minecraft Preview".
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

        // 1. Cached path - still there and valid?
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
    /// This is the closest equivalent to GDKLocator's exe-presence check - the
    /// "Users" folder is created by the game on first launch and is required for
    /// all per-user data to exist under it.
    /// </summary>
    private static bool IsValidDataRoot(string? path, bool isPreview)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Directory.Exists(path)) return false;
        if (!Directory.Exists(Path.Combine(path, UsersFolderName, SharedComMojangSubPath))) return false;

        // Reject if the folder name is explicitly the wrong edition.
        // Unknown/custom names (third-party launchers) pass through unchecked.
        var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var wrongEditionName = isPreview ? StableRootFolderName : PreviewRootFolderName;
        if (folderName.Equals(wrongEditionName, StringComparison.OrdinalIgnoreCase))
        {
            Trace.WriteLine($"[UserDataLocator] Rejected path - folder name indicates wrong edition: {folderName}");
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

        MainWindow.Log($"You can't use this feature without first telling the app where your {versionName} user data folder is located. " +
                       $"Click \"Locate {editionLabel} user data\" above, find and select the folder named \"{expectedFolderName}\" " +
                       $"- It's the one with a \"Users\" subfolder inside it.", LogLevel.Warning);

        return false;
    }
}

#endregion

# region TEXTURE SET TOOLS

// ══════════════════════════════════════════════════════════════════════════════
//  TextureSetHelper  ──  parsing, resolution, and virtual-bitmap creation
// ══════════════════════════════════════════════════════════════════════════════

public static class TextureSetHelper
{
    public enum TextureKind { Color, Mer, Normal, Heightmap }

    /// <summary>
    /// Discriminated union: either a real file path or an inline colour value.
    /// </summary>
    public sealed class TextureLayerValue
    {
        public string? FilePath { get; }

        public bool IsInline { get; }
        /// <summary>Parsed RGBA components (0-255). Always length 4 internally.</summary>
        public byte[] InlineRgba { get; } = Array.Empty<byte>();
        /// <summary>Number of components as originally written (3 or 4).</summary>
        public int InlineChannels { get; }
        /// <summary>True when the source was a hex string (e.g. "#B48CBE").</summary>
        public bool IsHex { get; }
        public JToken SourceToken { get; }

        private TextureLayerValue(JToken sourceToken, byte[] rgba, int originalChannels, bool isHex)
        {
            IsInline = true;
            SourceToken = sourceToken;
            InlineRgba = rgba;
            InlineChannels = originalChannels;   // the count as it appeared in the file
            IsHex = isHex;
        }

        private TextureLayerValue(string filePath)
        {
            FilePath = filePath;
            SourceToken = JValue.CreateNull();
        }

        public static TextureLayerValue FromFile(string path) => new(path);

        public static TextureLayerValue? TryParseInline(JToken token)
        {
            // Hex string
            if (token.Type == JTokenType.String)
            {
                var s = token.Value<string>()!.Trim();
                if (s.StartsWith('#') && TryParseHex(s, out var rgba, out var originalChannels))
                    return new TextureLayerValue(token, rgba, originalChannels, isHex: true);
                return null;
            }

            // Array of numbers (RGB triplet or RGBA quadruplet)
            if (token is JArray arr && arr.Count is 3 or 4)
            {
                var originalChannels = arr.Count;
                var comps = new byte[originalChannels];
                for (var i = 0; i < originalChannels; i++)
                {
                    if (!TryGetByte(arr[i], out comps[i]))
                        return null;
                }
                // Pad to 4 channels internally, but remember the original count
                var rgba = originalChannels == 4
                    ? comps
                    : new[] { comps[0], comps[1], comps[2], (byte)255 };
                return new TextureLayerValue(token, rgba, originalChannels, isHex: false);
            }

            return null;
        }

        private static bool TryParseHex(string hex, out byte[] rgba, out int originalChannels)
        {
            rgba = Array.Empty<byte>();
            originalChannels = 0;
            hex = hex.TrimStart('#');

            if (hex.Length == 6)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return false;
                rgba = new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)255 };
                originalChannels = 3;
                return true;
            }
            if (hex.Length == 8)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return false;
                rgba = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                originalChannels = 4;
                return true;
            }
            return false;
        }

        private static bool TryGetByte(JToken t, out byte b)
        {
            b = 0;
            double d;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                d = t.Value<double>();
            else if (t.Type == JTokenType.String && double.TryParse(t.Value<string>(), out d))
            { /* ok */ }
            else return false;

            b = (byte)Math.Clamp((int)Math.Round(d), 0, 255);
            return true;
        }

        /// <summary>Creates a 1×1 virtual Bitmap from the inline colour value.</summary>
        public Bitmap ToVirtualBitmap()
        {
            var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            bmp.SetPixel(0, 0, Color.FromArgb(InlineRgba[3], InlineRgba[0], InlineRgba[1], InlineRgba[2]));
            return bmp;
        }

        /// <summary>
        /// Serialises the (possibly modified) 1×1 bitmap back to exactly the format
        /// it was originally written in: RGB hex stays RGB hex, RGBA array stays RGBA
        /// array, etc. The alpha channel is always preserved from the bitmap as-is.
        /// </summary>
        public JToken SerializeVirtual(Bitmap bmp)
        {
            var c = bmp.GetPixel(0, 0);
            byte r = c.R, g = c.G, b = c.B, a = c.A;

            if (IsHex)
            {
                return InlineChannels == 3
                    ? new JValue($"#{r:X2}{g:X2}{b:X2}")
                    : new JValue($"#{r:X2}{g:X2}{b:X2}{a:X2}");
            }

            return InlineChannels == 3
                ? new JArray(r, g, b)
                : new JArray(r, g, b, a);
        }
    }

    public sealed class ResolvedTextureSet
    {
        public string JsonFilePath { get; init; } = "";
        public JObject RootJson { get; init; } = new();
        public JObject SetNode { get; init; } = new();

        public TextureLayerValue Color { get; init; } = null!;
        public TextureLayerValue? Mer { get; init; }
        public TextureLayerValue? NormalOrHeight { get; init; }
        public bool IsHeightmap { get; init; }
    }

    public sealed class LoadedTextureSet
    {
        public ResolvedTextureSet Resolved { get; init; } = null!;

        public Bitmap ColorBmp { get; set; } = null!;
        public bool ColorIsVirtual { get; init; }

        public Bitmap? MerBmp { get; set; }
        public bool MerIsVirtual { get; init; }

        public Bitmap? NormalBmp { get; set; }
        public bool NormalIsVirtual { get; init; }

        public bool ColorDirty { get; set; }
        public bool MerDirty { get; set; }
        public bool NormalDirty { get; set; }
    }

    private static readonly string[] SupportedExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// Scans a pack root, parses all .texture_set.json files, validates them
    /// per the Minecraft spec, and returns the valid resolved sets.
    /// </summary>
    public static IReadOnlyList<ResolvedTextureSet> ResolveTextureSets(string packRoot)
    {
        if (string.IsNullOrEmpty(packRoot) || !Directory.Exists(packRoot))
            return Array.Empty<ResolvedTextureSet>();

        var results = new List<ResolvedTextureSet>();

        foreach (var jsonFile in Directory.GetFiles(packRoot, "*.texture_set.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(jsonFile);
                var root = JObject.Parse(text);

                if (root.SelectToken("minecraft:texture_set") is not JObject set)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': missing minecraft:texture_set node.");
                    continue;
                }

                var folder = Path.GetDirectoryName(jsonFile)!;

                var colorToken = set["color"];
                if (colorToken == null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': no color layer defined.");
                    continue;
                }

                var colorLayer = ResolveLayer(folder, colorToken);
                if (colorLayer == null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': color layer could not be resolved.");
                    continue;
                }

                var merToken = set["metalness_emissive_roughness"];
                var mersToken = set["metalness_emissive_roughness_subsurface"];

                if (merToken != null && mersToken != null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': both MER and MERS defined (mutually exclusive).");
                    continue;
                }

                var merLayer = ResolveLayer(folder, merToken ?? mersToken);

                var normalToken = set["normal"];
                var heightmapToken = set["heightmap"];

                if (normalToken != null && heightmapToken != null)
                {
                    Trace.WriteLine($"[TUNER] Skipping '{jsonFile}': both normal and heightmap defined (mutually exclusive).");
                    continue;
                }

                var normalLayer = ResolveLayer(folder, normalToken);
                var heightmapLayer = ResolveLayer(folder, heightmapToken);
                var isHeightmap = heightmapToken != null;

                results.Add(new ResolvedTextureSet
                {
                    JsonFilePath = jsonFile,
                    RootJson = root,
                    SetNode = set,
                    Color = colorLayer,
                    Mer = merLayer,
                    NormalOrHeight = normalLayer ?? heightmapLayer,
                    IsHeightmap = isHeightmap,
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error resolving '{jsonFile}': {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Loads all bitmaps for a single resolved texture set. Virtual (inline) colours
    /// become 1×1 bitmaps and are flagged accordingly. Returns null (and leaves nothing
    /// allocated) if the color bitmap can't be loaded.
    ///
    /// This is deliberately a single-item operation rather than a batch: decoding an
    /// image from disk is real, sometimes-slow I/O work, and the orchestrator pipelines
    /// load → process → save per texture set (in parallel across texture sets) so that
    /// progress reporting and cancellation are granular to "one texture", not "one pack".
    /// </summary>
    public static LoadedTextureSet? LoadTextureSet(ResolvedTextureSet rs)
    {
        // previously, if the color layer loaded fine but the MER or normal
        // layer then *threw* while loading (rather than just returning null),
        // the already-loaded colorBmp/merBmp were never disposed - a real (if rare)
        // native GDI+ handle + memory leak. Track everything allocated here and
        // dispose it on any failure path via `finally`.
        Bitmap? colorBmp = null;
        Bitmap? merBmp = null;
        Bitmap? normalBmp = null;
        var success = false;

        try
        {
            colorBmp = LoadLayer(rs.Color);
            if (colorBmp == null)
            {
                Trace.WriteLine($"[TUNER] Skipping texture set '{rs.JsonFilePath}': color bitmap could not be loaded.");
                return null;
            }

            if (rs.Mer != null)
            {
                merBmp = LoadLayer(rs.Mer);
                if (merBmp == null)
                    Trace.WriteLine($"[TUNER] Warning for '{rs.JsonFilePath}': MER layer could not be loaded; MER processors will be skipped.");
            }

            if (rs.NormalOrHeight != null)
            {
                normalBmp = LoadLayer(rs.NormalOrHeight);
                if (normalBmp == null)
                    Trace.WriteLine($"[TUNER] Warning for '{rs.JsonFilePath}': normal/heightmap layer could not be loaded; normal processors will be skipped.");
            }

            var result = new LoadedTextureSet
            {
                Resolved = rs,
                ColorBmp = colorBmp,
                ColorIsVirtual = rs.Color.IsInline,
                MerBmp = merBmp,
                MerIsVirtual = rs.Mer?.IsInline ?? false,
                NormalBmp = normalBmp,
                NormalIsVirtual = rs.NormalOrHeight?.IsInline ?? false,
            };
            success = true;
            return result;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TUNER] Error loading texture set '{rs.JsonFilePath}': {ex.Message}");
            return null;
        }
        finally
        {
            if (!success)
            {
                colorBmp?.Dispose();
                merBmp?.Dispose();
                normalBmp?.Dispose();
            }
        }
    }

    /// <summary>Batch convenience wrapper kept for any other callers - loads every
    /// resolved set sequentially. Tuner's own pipeline calls LoadTextureSet directly
    /// per-item instead, so it can parallelize and report progress per texture.</summary>
    public static IReadOnlyList<LoadedTextureSet> LoadTextureSets(IReadOnlyList<ResolvedTextureSet> resolved)
    {
        var results = new List<LoadedTextureSet>(resolved.Count);
        foreach (var rs in resolved)
        {
            var lts = LoadTextureSet(rs);
            if (lts != null) results.Add(lts);
        }
        return results;
    }

    private static TextureLayerValue? ResolveLayer(string folder, JToken? token)
    {
        if (token == null) return null;

        var inline = TextureLayerValue.TryParseInline(token);
        if (inline != null) return inline;

        if (token.Type != JTokenType.String) return null;

        var name = token.Value<string>()!.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        var filePath = FindTextureFile(folder, name);
        return filePath != null ? TextureLayerValue.FromFile(filePath) : null;
    }

    private static Bitmap? LoadLayer(TextureLayerValue layer)
    {
        if (layer.IsInline)
            return layer.ToVirtualBitmap();

        if (!File.Exists(layer.FilePath!))
            return null;

        return Helpers.ReadImage(layer.FilePath!, false);
    }

    public static string? FindTextureFile(string folder, string textureName)
    {
        foreach (var ext in SupportedExtensions)
        {
            var target = Path.Combine(folder, textureName + ext);
            if (File.Exists(target))
                return target;

            try
            {
                var matches = Directory.GetFiles(folder, textureName + ext, SearchOption.TopDirectoryOnly);
                if (matches.Length > 0) return matches[0];
            }
            catch { /* access denied or directory missing */ }
        }

        return null;
    }

    /// <summary>
    /// Persists a loaded texture set's dirty bitmaps back to disk (or inline JSON).
    /// For real files: writes in the source format (TGA stays TGA, PNG stays PNG, etc.).
    /// For virtual bitmaps: patches the .texture_set.json in place.
    /// </summary>
    public static void SaveDirtyLayers(LoadedTextureSet lts)
    {
        var rs = lts.Resolved;
        var jsonDirty = false;

        if (lts.ColorDirty && lts.ColorBmp != null)
        {
            try
            {
                if (lts.ColorIsVirtual)
                {
                    rs.SetNode["color"] = rs.Color.SerializeVirtual(lts.ColorBmp);
                    jsonDirty = true;
                }
                else
                {
                    WriteBackBitmap(lts.ColorBmp, rs.Color.FilePath!);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error saving color layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (lts.MerDirty && lts.MerBmp != null && rs.Mer != null)
        {
            try
            {
                if (lts.MerIsVirtual)
                {
                    var merKey = rs.SetNode["metalness_emissive_roughness"] != null
                        ? "metalness_emissive_roughness"
                        : "metalness_emissive_roughness_subsurface";
                    rs.SetNode[merKey] = rs.Mer.SerializeVirtual(lts.MerBmp);
                    jsonDirty = true;
                }
                else
                {
                    WriteBackBitmap(lts.MerBmp, rs.Mer.FilePath!);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error saving MER layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (lts.NormalDirty && lts.NormalBmp != null && rs.NormalOrHeight != null)
        {
            try
            {
                if (lts.NormalIsVirtual)
                {
                    var normalKey = rs.IsHeightmap ? "heightmap" : "normal";
                    rs.SetNode[normalKey] = rs.NormalOrHeight.SerializeVirtual(lts.NormalBmp);
                    jsonDirty = true;
                }
                else
                {
                    WriteBackBitmap(lts.NormalBmp, rs.NormalOrHeight.FilePath!);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error saving normal/heightmap layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (jsonDirty)
        {
            try
            {
                File.WriteAllText(rs.JsonFilePath, rs.RootJson.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TUNER] Error writing JSON for '{rs.JsonFilePath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes a bitmap back to disk preserving the original file format.
    /// TGA  → TGA   PNG  → lossless 32-bpp ARGB PNG
    /// JPG  → maximum-quality JPEG   Other → TGA fallback
    /// </summary>
    private static void WriteBackBitmap(Bitmap bmp, string originalPath)
    {
        var ext = Path.GetExtension(originalPath).ToLowerInvariant();

        switch (ext)
        {
            case ".tga":
                Helpers.WriteImageAsTGA(bmp, originalPath);
                break;

            case ".png":
                {
                    // EnsureArgb32 returns the *same* instance when bmp is already
                    // Format32bppArgb (the common case). The old code wrapped that in a
                    // `using`, which disposed the caller's bitmap here - and then the
                    // orchestrator disposed it again a moment later. Bitmap.Dispose()
                    // happens to tolerate double-dispose, but it's fragile to rely on
                    // that; only dispose the canonical copy when it's actually a new object.
                    var canonical = EnsureArgb32(bmp);
                    try { canonical.Save(originalPath, ImageFormat.Png); }
                    finally { if (!ReferenceEquals(canonical, bmp)) canonical.Dispose(); }
                    break;
                }

            case ".jpg":
            case ".jpeg":
                {
                    var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                    if (jpegEncoder == null) goto default;

                    WarnIfAlphaWillBeLost(bmp, originalPath);

                    var qualityParam = new EncoderParameters(1);
                    qualityParam.Param[0] = new EncoderParameter(Encoder.Quality, 100L);

                    var canonical = EnsureArgb32(bmp);
                    try { canonical.Save(originalPath, jpegEncoder, qualityParam); }
                    finally { if (!ReferenceEquals(canonical, bmp)) canonical.Dispose(); }
                    break;
                }

            default:
                Helpers.WriteImageAsTGA(bmp, originalPath);
                break;
        }
    }

    /// <summary>
    /// JPEG has no alpha channel, so transparency in a layer written back as .jpg comes out
    /// fully opaque. Nothing here changes that - the source file's format is preserved on
    /// purpose, and a pack shipping .jpg usually has its reasons - this only makes the loss
    /// visible instead of silent. Harmless for an opaque color texture; on a MERS layer it
    /// means the subsurface channel is gone. Early-exits on the first non-opaque pixel, so
    /// the common (fully opaque) case costs one pass and the bad case costs almost nothing.
    /// </summary>
    private static void WarnIfAlphaWillBeLost(Bitmap bmp, string originalPath)
    {
        using var fb = new FastBitmap(bmp, writable: false);

        for (var y = 0; y < fb.Height; y++)
            for (var x = 0; x < fb.Width; x++)
                if (fb[x, y].A != 255)
                {
                    Trace.WriteLine($"[TUNER] '{Path.GetFileName(originalPath)}' has transparency but is a JPEG, which cannot store an alpha channel - it will be written back fully opaque. If this is a MERS layer, that is its subsurface data.");
                    return;
                }
    }

    private static Bitmap EnsureArgb32(Bitmap src)
    {
        if (src.PixelFormat == PixelFormat.Format32bppArgb)
            return src;

        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        return dst;
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
            if (codec.FormatID == format.Guid)
                return codec;
        return null;
    }
}

#endregion


# region FAST BITMAP

// ══════════════════════════════════════════════════════════════════════════════
//  FastBitmap  ──  LockBits-based pixel accessor, drop-in for GetPixel/SetPixel
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Replaces Bitmap.GetPixel/SetPixel for bulk pixel work. Each GetPixel/SetPixel
/// call on a System.Drawing.Bitmap round-trips through native GDI+ with format
/// checks and marshalling on every single pixel; for a 512x512 image that's a
/// quarter million native calls per full pass. FastBitmap instead locks the
/// bitmap once, bulk-copies its raw bytes into a managed buffer with a single
/// Marshal.Copy, and does all reads/writes against that plain byte[] (fast,
/// bounds-checked, no native calls). On Dispose it copies the buffer back
/// (only if opened writable) and unlocks.
///
/// Always requests Format32bppArgb regardless of the bitmap's real pixel
/// format - this exactly mirrors what GetPixel/SetPixel already did (they always
/// hand back/accept a plain ARGB Color regardless of underlying storage), so
/// output is unaffected: GDI+ performs the same implicit conversion on lock/unlock
/// that GetPixel/SetPixel performed internally per call.
///
/// No `unsafe` blocks are required, so no project/csproj changes are needed.
/// </summary>
public sealed class FastBitmap : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly BitmapData _data;
    private readonly byte[] _buffer;
    private readonly int _stride;
    private readonly bool _writable;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    public FastBitmap(Bitmap bitmap, bool writable)
    {
        _bitmap = bitmap;
        _writable = writable;
        Width = bitmap.Width;
        Height = bitmap.Height;

        _data = bitmap.LockBits(
            new Rectangle(0, 0, Width, Height),
            writable ? ImageLockMode.ReadWrite : ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        _stride = _data.Stride;
        _buffer = new byte[_stride * Height];
        Marshal.Copy(_data.Scan0, _buffer, 0, _buffer.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Color Get(int x, int y)
    {
        var i = y * _stride + x * 4;
        // Format32bppArgb byte order in memory is B, G, R, A.
        return Color.FromArgb(_buffer[i + 3], _buffer[i + 2], _buffer[i + 1], _buffer[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Set(int x, int y, Color c)
    {
        var i = y * _stride + x * 4;
        _buffer[i] = c.B;
        _buffer[i + 1] = c.G;
        _buffer[i + 2] = c.R;
        _buffer[i + 3] = c.A;
    }

    /// <summary>
    /// Deliberately named as an indexer rather than GetPixel/SetPixel: those names
    /// read exactly like the slow Bitmap API this class replaces, which caused real
    /// confusion during review even though the implementation underneath is entirely
    /// different (plain array access, no GDI+ calls). fb[x, y] makes it visually
    /// obvious it's not that.
    /// </summary>
    public Color this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Get(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Set(x, y, value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_writable)
                Marshal.Copy(_buffer, 0, _data.Scan0, _buffer.Length);
        }
        finally
        {
            _bitmap.UnlockBits(_data);
        }
    }
}

#endregion
