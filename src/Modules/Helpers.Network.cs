using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;
using static Vanilla_RTX_App.MainWindow;

namespace Vanilla_RTX_App.Modules;

public static partial class Helpers
{
    public static readonly HttpClient SharedHttpClient = CreateClient();
    public static readonly HttpClient UpdaterHttpClient = CreateClient("updater");

    static Helpers()
    {
        Trace.WriteLine("[HttpsHelper] SharedHttpClient and UpdaterHttpClient configured");
    }

    /// <summary>
    /// Builds one of the app's two long-lived clients.
    ///
    /// <para><b>Timeout is infinite on purpose.</b> HttpClient.Timeout covers the whole
    /// request including the response body, so any finite value is a cap on download <i>size
    /// over speed</i> rather than on hanging - an 11MB pack on a slow line would be cancelled
    /// mid-transfer. Per-request deadlines belong to the caller instead, via a
    /// CancellationToken (see <see cref="Download"/>, which takes its own timeout and turns it
    /// into one).</para>
    /// </summary>
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
            ? $"vanilla_rtx_app/{EnvironmentVariables.appVersion}"
            : $"vanilla_rtx_app_{component}/{EnvironmentVariables.appVersion} (https://github.com/Cubeir/Vanilla-RTX-App)";
    /// <summary>
    /// How long a transfer may make no progress at all before it is abandoned and retried.
    /// Separate from <c>timeout</c>, which bounds the whole attempt: a total deadline large
    /// enough for an 11MB pack on a slow line is far too large to notice a connection that was
    /// accepted and then went silent, and one small enough to notice that would cancel the pack.
    /// </summary>
    private static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether a failed request is one the server answered deliberately (4xx), as opposed to a
    /// transport failure or a server-side fault. <c>EnsureSuccessStatusCode</c> is what puts the
    /// code on the exception; a request that never reached a server carries none, and counts as
    /// transient.
    /// </summary>
    public static bool IsClientError(HttpRequestException ex) =>
        ex.StatusCode is { } code && (int)code >= 400 && (int)code < 500;

    /// <summary>
    /// Downloads a file with progress tracking and retry logic.
    /// Uses the shared HttpClient which is pre-configured.
    /// For custom timeout/headers, pass a custom HttpClient.
    /// Pass quiet: true to keep progress out of the UI log and send it to Trace instead.
    ///
    /// <para>Retries cover transient failures only. A 4xx is the server's considered answer -
    /// the file is gone, or we are being rate limited - and asking again immediately makes the
    /// second case worse, so those fail on the spot.</para>
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
            // Set once the destination is known and cleared once the file is complete, so
            // the finally below can tell a finished download from an abandoned one.
            string? partialPath = null;

            using var timeoutCts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            timeoutCts?.CancelAfter(timeout!.Value);

            // Layered on top of the total deadline rather than replacing it: this one is pushed
            // back by every chunk that arrives, so it only fires when nothing is coming through.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts?.Token ?? cancellationToken);
            stallCts.CancelAfter(NoProgressTimeout);
            var token = stallCts.Token;

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
                partialPath = savingLocation;

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
                    stallCts.CancelAfter(NoProgressTimeout);

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

                partialPath = null;

                Report("Download finished successfully.", LogLevel.Success);
                return (true, savingLocation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Report("Download cancelled by the caller.", LogLevel.Informational);
                return (false, null);
            }
            catch (HttpRequestException ex) when (IsClientError(ex))
            {
                Report($"Download refused by the server ({(int)ex.StatusCode!.Value} {ex.StatusCode}).", LogLevel.Error);
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
            finally
            {
                // A dropped connection leaves a half-written file behind, and the naming pass
                // above uniquifies around whatever already exists - so without this, retrying
                // a download that keeps failing leaves foo.json, foo-1.json, foo-2.json... in
                // the Downloads folder forever.
                if (partialPath is not null)
                {
                    try { File.Delete(partialPath); } catch { }
                }
            }
        }

        Report("Download failed after multiple attempts.", LogLevel.Error);
        return (false, null);
    }
}
