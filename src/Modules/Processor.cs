using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using static Vanilla_RTX_App.TunerVariables;
using static Vanilla_RTX_App.TunerVariables.Persistent;
using static Vanilla_RTX_App.Modules.Helpers;
using static Vanilla_RTX_App.Modules.ProcessorVariables;
using System.Diagnostics;

namespace Vanilla_RTX_App.Modules;

public static class ProcessorVariables
{
    public const bool FOG_UNIFORM_HEIGHT = false;
    public const double EMISSIVE_EXCESS_INTENSITY_DAMPEN = 0.1;
}


// TODO: Idea was to refactor the processor so it loads all files first, then processes them in multiple passes in memory instead of
// constantly loading and saving, but the tuning already happens quite fast (with the files being raw tgas) so it may not be worth
// the added complexity of defining which textures will be needed to be retrieved and all that
// Still, if a kind soul out there wants to take a stab at it, be my guest.
// The issue is that, you'd have to load everything in memory regardless for that to happen
// Right now the processors, if called, GET WHAT THEY WANT, the mutliple individual passes can be beneficial

public class Processor
{
    private struct PackInfo
    {
        public string Name;
        public string Path;
        public bool Enabled;

        public PackInfo(string name, string path, bool enabled)
        {
            Name = name;
            Path = path;
            Enabled = enabled;
        }
    }

    public static void TuneSelectedPacks()
    {
        if (RuntimeFlags.Set("Has_Told_Tuning_Options_Thingy"))
        {
            MainWindow.Log("Options left at default will be skipped.", MainWindow.LogLevel.Informational);
        }
        var packs = new[]
        {
        new PackInfo("Vanilla RTX", VanillaRTXLocation, IsVanillaRTXEnabled),
        new PackInfo("Vanilla RTX Normals", VanillaRTXNormalsLocation, IsNormalsEnabled),
        new PackInfo("Vanilla RTX Opus", VanillaRTXOpusLocation, IsOpusEnabled),
        new PackInfo(CustomPackDisplayName, CustomPackLocation, !string.IsNullOrEmpty(CustomPackLocation))
        };


        // Remove custom pack path if it points to the same location of an already selected pack
        string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var customPackNormalizedPath = NormalizePath(CustomPackLocation);

        bool isDuplicate = packs
            .Take(packs.Length - 1)
            .Where(p => p.Enabled)
            .Any(p => NormalizePath(p.Path).Equals(customPackNormalizedPath, StringComparison.OrdinalIgnoreCase));
        if (isDuplicate)
        {
            MainWindow.Log($"{CustomPackDisplayName} was selected twice, but will only be processed once!", MainWindow.LogLevel.Warning);
            packs = packs.Take(packs.Length - 1).ToArray();
        }

        MainWindow.Log($"Tuning selected {((IsVanillaRTXEnabled ? 1 : 0) + (IsNormalsEnabled ? 1 : 0) + (IsOpusEnabled ? 1 : 0) + (!string.IsNullOrEmpty(CustomPackLocation) ? 1 : 0) == 1 ? "package" : "packages")}...", MainWindow.LogLevel.Lengthy);

        if (FogMultiplier != Defaults.FogMultiplier)
        {
            foreach (var p in packs)
            {
                ProcessFog(p);
                ProcessFog(p, true);
            }
        }

        if (EmissivityMultiplier != Defaults.EmissivityMultiplier || AddEmissivityAmbientLight != Defaults.AddEmissivityAmbientLight)
        {
            foreach (var p in packs)
                ProcessEmissivity(p);
        }

        if (LazifyNormalAlpha != Defaults.LazifyNormalAlpha)
        {
            foreach (var p in packs)
                ProcessLazify(p);
        }

        if (NormalIntensity != Defaults.NormalIntensity)
        {
            foreach (var p in packs)
                ProcessNormalIntensity(p);
        }

        if (RoughnessControlValue != Defaults.RoughnessControlValue)
        {
            foreach (var p in packs)
                ProcessRoughness(p);
        }

        if (MaterialNoiseOffset != Defaults.MaterialNoiseOffset)
        {
            foreach (var p in packs)
                ProcessMaterialGrain(p);
        }
    }


    // TODO: make processors return reasons of their failure for easier debugging at the end without touching UI thread directly.
    // this is got to become a part of the larger logging overhaul down the line (gradual logger thing from public string)
    // Also make them log any oddities in Vanilla RTX (whether it be size, opacity, etc...) as warnings, serves dual purpose that way
    #region ------------------- Processors


    private static void ProcessFog(PackInfo pack, bool processWaterOnly = false)
    {
        const double MIN_VALUE_THRESHOLD = 0.00000001;
        const int DECIMAL_PRECISION = 6;

        if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
            return;

        var fogDirectories = Directory
            .GetDirectories(pack.Path, "*", SearchOption.AllDirectories)
            .Where(d => string.Equals(Path.GetFileName(d), "fogs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!fogDirectories.Any() && !processWaterOnly)
        {
            MainWindow.Log($"{pack.Name}: does not contain fog files.");
            return;
        }

        var files = fogDirectories
            .SelectMany(dir => {
                try { return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly); }
                catch { return Enumerable.Empty<string>(); }
            })
            .ToList();

        if (!files.Any())
            return;

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var root = JObject.Parse(text);

                var volumetric = root.SelectToken("minecraft:fog_settings.volumetric") as JObject;
                if (volumetric == null)
                    continue;

                var modified = processWaterOnly
                    ? ProcessWaterCoefficients(volumetric)
                    : ProcessAirDensityAndScattering(volumetric);

                if (modified)
                {
                    var jsonString = root.ToString(Newtonsoft.Json.Formatting.Indented);

                    // Only fix: convert any scientific notation to fixed-point
                    jsonString = RemoveScientificNotation(jsonString);

                    File.WriteAllText(file, jsonString);
                }
            }
            catch (Exception ex)
            {
                // MainWindow.Log($"{pack.Name}: error processing {Path.GetFileName(file)} — {ex.Message}");
            }
        }

        // === AIR DENSITY AND SCATTERING PROCESSING ===
        bool ProcessAirDensityAndScattering(JObject volumetric)
        {
            var modified = false;
            var density = volumetric.SelectToken("density") as JObject;
            if (density == null)
                return false;

            var airSection = density.SelectToken("air") as JObject;
            var weatherSection = density.SelectToken("weather") as JObject;

            var densityValues = new List<(string name, JObject section, double original, double multiplied)>();
            var allDensities = new List<double>();

            // Collect air density
            double airDensityFinal = 0.0;
            if (airSection != null && TryGetNumericValue(airSection.SelectToken("max_density"), out var airDensity))
            {
                allDensities.Add(airDensity);
                if (Math.Abs(airDensity) < 0.0001)
                {
                    airDensityFinal = CalculateNewDensity(airDensity, FogMultiplier);
                    if (Math.Abs(airDensityFinal - airDensity) >= MIN_VALUE_THRESHOLD)
                    {
                        airSection["max_density"] = ClampAndRound(airDensityFinal);
                        modified = true;
                    }
                }
                else
                {
                    var multiplied = airDensity * FogMultiplier;
                    densityValues.Add(("air", airSection, airDensity, multiplied));
                    airDensityFinal = multiplied;
                }
            }

            // Collect weather density
            double weatherDensityFinal = 0.0;
            if (weatherSection != null && TryGetNumericValue(weatherSection.SelectToken("max_density"), out var weatherDensity))
            {
                allDensities.Add(weatherDensity);
                if (Math.Abs(weatherDensity) < 0.0001)
                {
                    weatherDensityFinal = CalculateNewDensity(weatherDensity, FogMultiplier);
                    if (Math.Abs(weatherDensityFinal - weatherDensity) >= MIN_VALUE_THRESHOLD)
                    {
                        weatherSection["max_density"] = ClampAndRound(weatherDensityFinal);
                        modified = true;
                    }
                }
                else
                {
                    var multiplied = weatherDensity * FogMultiplier;
                    densityValues.Add(("weather", weatherSection, weatherDensity, multiplied));
                    weatherDensityFinal = multiplied;
                }
            }

            // Apply proportional scaling if any density exceeds 1.0
            if (densityValues.Any())
            {
                var maxMultiplied = densityValues.Max(x => x.multiplied);
                var scaleFactor = maxMultiplied > 1.0 ? 1.0 / maxMultiplied : 1.0;

                foreach (var (name, section, original, multiplied) in densityValues)
                {
                    var finalValue = multiplied * scaleFactor;
                    section["max_density"] = ClampAndRound(finalValue);
                    modified = true;

                    if (name == "air")
                        airDensityFinal = finalValue;
                    else if (name == "weather")
                        weatherDensityFinal = finalValue;
                }
            }

            // Calculate average density for scattering adjustment
            var finalDensities = new List<double>();
            if (airDensityFinal > 0) finalDensities.Add(Math.Min(airDensityFinal, 1.0));
            if (weatherDensityFinal > 0) finalDensities.Add(Math.Min(weatherDensityFinal, 1.0));

            var avgDensity = finalDensities.Any() ? finalDensities.Average() :
                             (allDensities.Any() ? allDensities.Average() : 0.0);
            var proximityToMax = Math.Min(avgDensity, 1.0);

            // Process air scattering if there's meaningful density
            if (proximityToMax > 0.0)
            {
                var overage = FogMultiplier - 1.0;
                var dampenedOverage = overage * 0.25 * proximityToMax;
                var scatteringMultiplier = 1.0 + dampenedOverage;

                var airCoefficients = volumetric.SelectToken("media_coefficients.air") as JObject;
                var scatteringArray = airCoefficients?.SelectToken("scattering") as JArray;

                if (scatteringArray != null && scatteringArray.Count >= 3)
                {
                    modified |= ProcessRgbArray(scatteringArray, scatteringMultiplier);
                }
            }

            // Process uniform density settings
            modified |= MakeDensityUniform(airSection);
            modified |= MakeDensityUniform(weatherSection);

            return modified;
        }

        // === WATER COEFFICIENTS PROCESSING ===
        bool ProcessWaterCoefficients(JObject volumetric)
        {
            var modified = false;

            // Determine dampening based on air/weather density
            var airDensity = GetDensityValue(volumetric, "density.air.max_density");
            var weatherDensity = GetDensityValue(volumetric, "density.weather.max_density");

            var densities = new[] { airDensity, weatherDensity }
                .Where(d => d > 0)
                .Select(d => Math.Min(d, 1.0))
                .ToList();

            var avgDensity = densities.Any() ? densities.Average() : 0.5;
            var proximityToMin = 1.0 - avgDensity;

            var overage = FogMultiplier - 1.0;
            var dampenedOverage = overage * 0.1 * Math.Max(proximityToMin, 0.25);
            var waterMultiplier = 1.0 + dampenedOverage;

            var waterCoefficients = volumetric.SelectToken("media_coefficients.water") as JObject;
            if (waterCoefficients == null)
                return false;

            // Process scattering
            var scatteringArray = waterCoefficients.SelectToken("scattering") as JArray;
            if (scatteringArray != null && scatteringArray.Count >= 3)
            {
                modified |= ProcessRgbArray(scatteringArray, waterMultiplier);
            }

            // Process absorption
            var absorptionArray = waterCoefficients.SelectToken("absorption") as JArray;
            if (absorptionArray != null && absorptionArray.Count >= 3)
            {
                modified |= ProcessRgbArray(absorptionArray, waterMultiplier);
            }

            return modified;
        }

        // === HELPER METHODS ===
        bool ProcessRgbArray(JArray rgbArray, double multiplier)
        {
            var rgbValues = new double[3];

            for (var i = 0; i < 3; i++)
            {
                if (!TryGetNumericValue(rgbArray[i], out rgbValues[i]))
                    return false;

                rgbValues[i] *= multiplier;
            }

            var maxRgb = rgbValues.Max();
            if (maxRgb > 1.0)
            {
                var scaleFactor = 1.0 / maxRgb;
                for (var i = 0; i < 3; i++)
                    rgbValues[i] *= scaleFactor;
            }

            for (var i = 0; i < 3; i++)
            {
                rgbArray[i] = ClampAndRound(rgbValues[i]);
            }

            return true;
        }

        bool MakeDensityUniform(JObject section)
        {
            if (section == null || !FOG_UNIFORM_HEIGHT)
                return false;

            var hasHeightFields = section.SelectToken("max_density_height") != null ||
                                 section.SelectToken("zero_density_height") != null;
            var isUniform = section.SelectToken("uniform")?.Value<bool>() ?? false;

            if (hasHeightFields && !isUniform)
            {
                section.Remove("max_density_height");
                section.Remove("zero_density_height");
                section["uniform"] = true;
                return true;
            }

            return false;
        }

        bool TryGetNumericValue(JToken token, out double value)
        {
            value = 0.0;
            if (token == null)
                return false;

            return token.Type switch
            {
                JTokenType.Float or JTokenType.Integer => (value = token.Value<double>()) >= 0,
                JTokenType.String => double.TryParse(token.Value<string>(), out value),
                _ => false
            };
        }

        double GetDensityValue(JObject volumetric, string path)
        {
            var token = volumetric.SelectToken(path);
            return TryGetNumericValue(token, out var value) ? value : 0.0;
        }

        double ClampAndRound(double value)
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            var rounded = Math.Round(clamped, DECIMAL_PRECISION);

            // Zero out values below threshold
            if (Math.Abs(rounded) < MIN_VALUE_THRESHOLD)
                return 0.0;

            return rounded;
        }

        double CalculateNewDensity(double currentDensity, double fogMultiplier)
        {
            if (Math.Abs(currentDensity) < 0.0001)
            {
                return fogMultiplier <= 1.0
                    ? Math.Clamp(fogMultiplier, 0.0, 1.0)
                    : Math.Clamp(fogMultiplier / 10.0, 0.0, 1.0);
            }

            return Math.Clamp(currentDensity * fogMultiplier, 0.0, 1.0);
        }

        string RemoveScientificNotation(string jsonString)
        {
            // ONLY match scientific notation that appears as a JSON number value
            // NOT inside strings (like hex colors #10E2 etc)
            // Pattern: number with e/E notation that's preceded by : or [ and followed by , ] or newline
            var pattern = @"(?<=:\s*|,\s*|\[\s*)(-?\d+\.?\d*[eE][+-]?\d+)(?=\s*[,\]\}]|\s*$)";

            return System.Text.RegularExpressions.Regex.Replace(jsonString, pattern, match =>
            {
                if (double.TryParse(match.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    // Zero out tiny values
                    if (Math.Abs(value) < MIN_VALUE_THRESHOLD)
                        return "0.0";

                    // Round to precision
                    var rounded = Math.Round(value, DECIMAL_PRECISION);

                    if (Math.Abs(rounded) < MIN_VALUE_THRESHOLD)
                        return "0.0";

                    // Format with up to DECIMAL_PRECISION decimals, removing trailing zeros
                    var formatted = rounded.ToString($"0.{new string('#', DECIMAL_PRECISION)}",
                        System.Globalization.CultureInfo.InvariantCulture);

                    return formatted;
                }

                return match.Value;
            });
        }
    }



    private static void ProcessEmissivity(PackInfo pack)
    {
        if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
            return;

        var files = TextureSetHelper.RetrieveFilesFromTextureSets(pack.Path, TextureSetHelper.TextureType.Mer);

        if (!files.Any())
        {
            MainWindow.Log($"{pack.Name}: no MERS texture files found from texture sets.");
            return;
        }

        var userMult = EmissivityMultiplier;
        foreach (var file in files)
        {
            try
            {
                using var bmp = ReadImage(file, false);
                var width = bmp.Width;
                var height = bmp.Height;
                var wroteBack = false;

                // First pass: emissivity processing
                if (userMult != 1.0)
                {
                    // Max green value within image
                    var maxGreen = 0;
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            int g = bmp.GetPixel(x, y).G;
                            if (g > maxGreen) maxGreen = g;
                        }
                    }

                    // Only process if there are green pixels
                    if (maxGreen > 0)
                    {
                        // Calculate how much we can multiply before hitting 255
                        var ratio = 255.0 / maxGreen;
                        var neededMult = ratio;
                        var effectiveMult = userMult < neededMult ? userMult : neededMult;

                        // Calculate excess - the portion of multiplier we couldn't directly apply
                        var excess = Math.Max(0, userMult - effectiveMult);

                        // Dampen excess TOWARDS 1.0
                        // EMISSIVE_EXCESS_INTENSITY_DAMPEN represents how much we move towards 1.0
                        // 0.1 = 90% dampening (move 90% closer to 1.0)
                        // 0.5 = 50% dampening (move 50% closer to 1.0)
                        // 0.9 = 10% dampening (move 10% closer to 1.0)
                        var excessOverage = excess - 1.0;
                        var dampenedExcessOverage = excessOverage * EMISSIVE_EXCESS_INTENSITY_DAMPEN;
                        var dampenedExcess = 1.0 + dampenedExcessOverage;

                        // Process existing emissivity
                        for (var y = 0; y < height; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                var origColor = bmp.GetPixel(x, y);
                                int origG = origColor.G;
                                if (origG == 0)
                                    continue;

                                // Apply effective multiplier (the part we can use fully)
                                var newG = origG * effectiveMult;

                                // Apply dampened excess (the overflow part, dampened towards 1.0)
                                if (excess > 0)
                                {
                                    newG += origG * (dampenedExcess - 1.0);
                                }

                                // Custom rounding logic: if < 127.5, round up; if >= 127.5, round down
                                int finalG;
                                if (newG < 127.5)
                                {
                                    finalG = (int)Math.Ceiling(newG);
                                }
                                else
                                {
                                    finalG = (int)Math.Floor(newG);
                                }

                                finalG = Math.Clamp(finalG, 0, 255);

                                if (finalG != origG)
                                {
                                    wroteBack = true;
                                    var newColor = Color.FromArgb(origColor.A, origColor.R, finalG, origColor.B);
                                    bmp.SetPixel(x, y, newColor);
                                }
                            }
                        }
                    }
                }

                // Second pass: Add ambient light to all pixels
                if (AddEmissivityAmbientLight)
                {
                    // Determine & apply ambient light amount (Multiplier rounded up, plus one)
                    var ambientAmount = (int)Math.Ceiling(userMult) + 1;

                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var origColor = bmp.GetPixel(x, y);
                            var newG = Math.Clamp(origColor.G + ambientAmount, 0, 255);

                            if (newG != origColor.G)
                            {
                                wroteBack = true;
                                var newColor = Color.FromArgb(origColor.A, origColor.R, newG, origColor.B);
                                bmp.SetPixel(x, y, newColor);
                            }
                        }
                    }
                }

                if (wroteBack)
                {
                    WriteImageAsTGA(bmp, file);
                    // MainWindow.Log($"{packName}: updated emissivity in {Path.GetFileName(file)}.");
                }
                else
                {
                    // MainWindow.Log($"{packName}: no emissivity changes in {Path.GetFileName(file)}.");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"{pack.Name}: error processing {Path.GetFileName(file)} — {ex.Message}");
                // Updates UI which can cause freezing if too many files give error, but it is worth it as logs will appear in the end
            }
        }
    }



    private static void ProcessNormalIntensity(PackInfo pack)
    {
        if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
            return;

        // Get normal and heightmap files from texture sets
        var allNormalFiles = TextureSetHelper.RetrieveFilesFromTextureSets(pack.Path, TextureSetHelper.TextureType.Normal);
        var allHeightmapFiles = TextureSetHelper.RetrieveFilesFromTextureSets(pack.Path, TextureSetHelper.TextureType.Heightmap);

        // Check if we have anything to process at all
        if (!allNormalFiles.Any() && !allHeightmapFiles.Any())
        {
            MainWindow.Log($"{pack.Name}: no normal or heightmap texture files found from texture sets.", MainWindow.LogLevel.Warning);
            return;
        }

        // Process heightmaps first
        ProcessHeightmapsIntensity(allHeightmapFiles);

        var files = new List<string>();

        foreach (var file in allNormalFiles)
        {
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
            var fileDir = Path.GetDirectoryName(file);

            // Check if a double-normal variant exists (some blocks already end with _normal suffix)
            var possibleExtensions = new[] { ".tga", ".png", ".jpg", ".jpeg" };
            string? doubleNormalPath = null;

            foreach (var ext in possibleExtensions)
            {
                var testPath = Path.Combine(fileDir, fileNameWithoutExt + "_normal" + ext);
                if (File.Exists(testPath))
                {
                    doubleNormalPath = testPath;
                    break;
                }
            }

            if (doubleNormalPath != null)
            {
                // Use the double-normal version (the real normal map)
                if (!files.Contains(doubleNormalPath))
                    files.Add(doubleNormalPath);
            }
            else
            {
                // No double-normal exists, so this normal file is the actual normal map
                files.Add(file);
            }
        }

        if (!files.Any())
        {
            // No normal files to process, but that's okay if we had heightmaps
            // MainWindow.Log($"{pack.Name}: no processable normal files found.");
            return;
        }

        var intensityPercent = NormalIntensity / 100.0; // percentage -> multiplier

        foreach (var file in files)
        {
            try
            {
                using var bmp = ReadImage(file, false);
                var width = bmp.Width;
                var height = bmp.Height;
                bool wroteBack = false;

                // For reduction (intensity < 1.0), simple linear scaling toward neutral
                if (intensityPercent <= 1.0)
                {
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var origColor = bmp.GetPixel(x, y);

                            // Simple linear interpolation toward 128 (neutral)
                            var newR = (int)Math.Round(128 + (origColor.R - 128) * intensityPercent);
                            var newG = (int)Math.Round(128 + (origColor.G - 128) * intensityPercent);

                            newR = Math.Clamp(newR, 0, 255);
                            newG = Math.Clamp(newG, 0, 255);

                            if (newR != origColor.R || newG != origColor.G)
                            {
                                wroteBack = true;
                                var newColor = Color.FromArgb(origColor.A, newR, newG, origColor.B);
                                bmp.SetPixel(x, y, newColor);
                            }
                        }
                    }
                }
                else
                {
                    // For increase (intensity > 1.0), use proportional compression approach

                    // Find the maximum deviation that would occur after scaling
                    double maxIdealDeviation = 0;
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var pixel = bmp.GetPixel(x, y);
                            var rDev = Math.Abs((pixel.R - 128.0) * intensityPercent);
                            var gDev = Math.Abs((pixel.G - 128.0) * intensityPercent);
                            var maxDev = Math.Max(rDev, gDev);
                            if (maxDev > maxIdealDeviation)
                                maxIdealDeviation = maxDev;
                        }
                    }

                    if (maxIdealDeviation == 0)
                    {
                        // Flat normal map, nothing to do
                        continue;
                    }

                    // Calculate compression ratio to fit within valid range
                    var compressionRatio = maxIdealDeviation > 127.0 ? 127.0 / maxIdealDeviation : 1.0;

                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var origColor = bmp.GetPixel(x, y);

                            // Apply intensity then compression
                            var idealR = 128.0 + (origColor.R - 128.0) * intensityPercent * compressionRatio;
                            var idealG = 128.0 + (origColor.G - 128.0) * intensityPercent * compressionRatio;

                            var newR = (int)Math.Round(idealR);
                            var newG = (int)Math.Round(idealG);

                            newR = Math.Clamp(newR, 0, 255);
                            newG = Math.Clamp(newG, 0, 255);

                            if (newR != origColor.R || newG != origColor.G)
                            {
                                wroteBack = true;
                                var newColor = Color.FromArgb(origColor.A, newR, newG, origColor.B);
                                bmp.SetPixel(x, y, newColor);
                            }
                        }
                    }
                }

                if (wroteBack)
                {
                    WriteImageAsTGA(bmp, file);
                    // MainWindow.Log($"{packName}: updated normal intensity in {Path.GetFileName(file)}.");
                }
                else
                {
                    // MainWindow.Log($"{packName}: no normal intensity changes in {Path.GetFileName(file)}.");
                }
            }
            catch (Exception ex)
            {
                // MainWindow.Log($"{packName}: error processing {Path.GetFileName(file)} — {ex.Message}");
            }
        }

        // Detail-preserving heightmap contrast adjustment
        void ProcessHeightmapsIntensity(string[] heightmapFiles)
        {
            var userIntensity = NormalIntensity / 100.0;

            foreach (var file in heightmapFiles)
            {
                try
                {
                    using var bmp = ReadImage(file, false);
                    var width = bmp.Width;
                    var height = bmp.Height;

                    // Find min/max values in the heightmap
                    int minGray = 255, maxGray = 0;
                    for (var y = 0; y < height; y++)
                        for (var x = 0; x < width; x++)
                        {
                            var gray = bmp.GetPixel(x, y).R;
                            if (gray < minGray) minGray = gray;
                            if (gray > maxGray) maxGray = gray;
                        }

                    double currentSpan = maxGray - minGray;
                    if (currentSpan == 0)
                    {
                        // Flat image, nothing to do
                        continue;
                    }

                    // Calculate the ideal new span based on user intensity
                    var idealSpan = currentSpan * userIntensity;

                    // Determine the actual span we can achieve (clamped to 255)
                    var actualSpan = Math.Min(idealSpan, 255.0);

                    // Calculate the center point for the transformation
                    var currentCenter = (minGray + maxGray) / 2.0;
                    var newCenter = 127.5; // Target center of 0-255 range

                    // If ideal span exceeds 255, we need to compress proportionally
                    var compressionRatio = actualSpan / Math.Max(idealSpan, actualSpan);

                    bool hasChanges = false;

                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var origColor = bmp.GetPixel(x, y);
                            var gray = origColor.R;

                            // Calculate deviation from current center
                            var deviation = gray - currentCenter;

                            // Apply intensity scaling with compression if needed
                            var newDeviation = deviation * userIntensity * compressionRatio;

                            // Calculate final value around new center
                            var newGray = newCenter + newDeviation;

                            // Clamp and round
                            var finalGray = (int)Math.Round(newGray);
                            finalGray = Math.Clamp(finalGray, 0, 255);

                            if (finalGray != gray)
                            {
                                hasChanges = true;
                                var newColor = Color.FromArgb(origColor.A, finalGray, finalGray, finalGray);
                                bmp.SetPixel(x, y, newColor);
                            }
                        }
                    }

                    if (hasChanges)
                    {
                        WriteImageAsTGA(bmp, file);
                    }
                }
                catch (Exception ex)
                {
                    // MainWindow.Log($"{pack.Name}: error processing heightmap {Path.GetFileName(file)} — {ex.Message}");
                }
            }
        }
    }


    
    private static void ProcessLazify(PackInfo pack)
    {
        if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
            return;

        // Get paired color and heightmap textures from texture sets
        var heightmapPairs = TextureSetHelper.RetrieveTextureSetPairs(pack.Path, TextureSetHelper.TextureType.Color, TextureSetHelper.TextureType.Heightmap);
        var normalmapPairs = TextureSetHelper.RetrieveTextureSetPairs(pack.Path, TextureSetHelper.TextureType.Color, TextureSetHelper.TextureType.Normal);

        if (!heightmapPairs.Any() && !normalmapPairs.Any())
        {
            MainWindow.Log($"{pack.Name}: no texture sets with color and heightmap/normal found.", MainWindow.LogLevel.Warning);
            return;
        }

        var alpha = LazifyNormalAlpha;

        // Process heightmaps
        foreach (var (colormapFile, heightmapFile) in heightmapPairs)
        {
            if (string.IsNullOrEmpty(heightmapFile))
            {
                Trace.WriteLine($"{pack.Name}: heightmap not found for {Path.GetFileName(colormapFile)}; skipped.");
                continue;
            }

            try
            {
                using var heightmapBmp = ReadImage(heightmapFile, false);
                using var colormapBmp = ReadImage(colormapFile, false);

                var width = heightmapBmp.Width;
                var height = heightmapBmp.Height;

                if (colormapBmp.Width != width || colormapBmp.Height != height)
                {
                    Trace.WriteLine($"{pack.Name}: dimension mismatch between heightmap and colormap for {Path.GetFileName(heightmapFile)}; skipped.");
                    continue;
                }

                // Apply edge padding to transparent pixels
                var paddedColormap = ApplyEdgePadding(colormapBmp);

                // Convert to greyscale and find min/max
                var greyscaleValues = new byte[width, height];
                byte minValue = 255;
                byte maxValue = 0;

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var color = paddedColormap[x, y];
                        var greyValue = (byte)(0.299 * color.R + 0.587 * color.G + 0.114 * color.B);
                        greyscaleValues[x, y] = greyValue;

                        if (greyValue < minValue) minValue = greyValue;
                        if (greyValue > maxValue) maxValue = greyValue;
                    }
                }

                // Stretch/Maximize
                var stretchedValues = new byte[width, height];
                double range = maxValue - minValue;

                if (range == 0)
                {
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            stretchedValues[x, y] = 128;
                        }
                    }
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var normalized = (greyscaleValues[x, y] - minValue) / range;
                            stretchedValues[x, y] = (byte)(normalized * 255);
                        }
                    }
                }

                // Overlay stretched heightmap on original heightmap
                var wroteBack = false;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var origColor = heightmapBmp.GetPixel(x, y);
                        var newHeightValue = stretchedValues[x, y];

                        var blendedValue = (alpha * newHeightValue + (255 - alpha) * origColor.R) / 255;
                        var finalValue = (byte)Math.Clamp(blendedValue, 0, 255);

                        if (finalValue != origColor.R)
                        {
                            wroteBack = true;
                            var newColor = Color.FromArgb(origColor.A, finalValue, finalValue, finalValue);
                            heightmapBmp.SetPixel(x, y, newColor);
                        }
                    }
                }

                if (wroteBack)
                {
                    WriteImageAsTGA(heightmapBmp, heightmapFile);
                    Trace.WriteLine($"{pack.Name}: updated heightmap in {Path.GetFileName(heightmapFile)}.");
                }
                else
                {
                    Trace.WriteLine($"{pack.Name}: no heightmap changes in {Path.GetFileName(heightmapFile)}.");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"{pack.Name}: error processing {Path.GetFileName(heightmapFile)} — {ex.Message}");
            }
        }

        // Process normal maps
        foreach (var (colormapFile, normalmapFile) in normalmapPairs)
        {
            if (string.IsNullOrEmpty(normalmapFile))
            {
                Trace.WriteLine($"{pack.Name}: normal map not found for {Path.GetFileName(colormapFile)}; skipped.");
                continue;
            }

            try
            {
                using var normalmapBmp = ReadImage(normalmapFile, false);
                using var colormapBmp = ReadImage(colormapFile, false);

                var width = normalmapBmp.Width;
                var height = normalmapBmp.Height;

                if (colormapBmp.Width != width || colormapBmp.Height != height)
                {
                    Trace.WriteLine($"{pack.Name}: dimension mismatch between normal map and colormap for {Path.GetFileName(normalmapFile)}; skipped.");
                    continue;
                }

                // Apply edge padding to transparent pixels
                var paddedColormap = ApplyEdgePadding(colormapBmp);

                // Convert to greyscale and find min/max
                var greyscaleValues = new byte[width, height];
                byte minValue = 255;
                byte maxValue = 0;

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var color = paddedColormap[x, y];
                        var greyValue = (byte)(0.299 * color.R + 0.587 * color.G + 0.114 * color.B);
                        greyscaleValues[x, y] = greyValue;

                        if (greyValue < minValue) minValue = greyValue;
                        if (greyValue > maxValue) maxValue = greyValue;
                    }
                }

                // Stretch/Maximize
                var stretchedValues = new byte[width, height];
                double range = maxValue - minValue;

                if (range == 0)
                {
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            stretchedValues[x, y] = 128;
                        }
                    }
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var normalized = (greyscaleValues[x, y] - minValue) / range;
                            stretchedValues[x, y] = (byte)(normalized * 255);
                        }
                    }
                }

                // Create 3x3 tiled heightmap for seamless normal generation
                var expandedWidth = width * 3;
                var expandedHeight = height * 3;
                var expandedHeightmap = new byte[expandedWidth, expandedHeight];

                // Fill with 9 tiles (3x3 grid)
                for (var tileY = 0; tileY < 3; tileY++)
                {
                    for (var tileX = 0; tileX < 3; tileX++)
                    {
                        for (var y = 0; y < height; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                var destX = tileX * width + x;
                                var destY = tileY * height + y;
                                expandedHeightmap[destX, destY] = stretchedValues[x, y];
                            }
                        }
                    }
                }

                // Generate normal map for the ENTIRE 3x3 tiled image
                var expandedNormals = new (byte r, byte g)[expandedWidth, expandedHeight];

                for (var y = 1; y < expandedHeight - 1; y++) // Skip outer edges (can't do 3x3 kernel there)
                {
                    for (var x = 1; x < expandedWidth - 1; x++)
                    {
                        // Sobel kernels for X and Y gradients
                        var gx =
                            -1 * expandedHeightmap[x - 1, y - 1] + 1 * expandedHeightmap[x + 1, y - 1] +
                            -2 * expandedHeightmap[x - 1, y] + 2 * expandedHeightmap[x + 1, y] +
                            -1 * expandedHeightmap[x - 1, y + 1] + 1 * expandedHeightmap[x + 1, y + 1];

                        var gy =
                            -1 * expandedHeightmap[x - 1, y - 1] - 2 * expandedHeightmap[x, y - 1] - 1 * expandedHeightmap[x + 1, y - 1] +
                             1 * expandedHeightmap[x - 1, y + 1] + 2 * expandedHeightmap[x, y + 1] + 1 * expandedHeightmap[x + 1, y + 1];

                        // Normalize gradients to 0-255 range for DirectX format
                        var normalX = gx / (8.0 * 255.0);
                        var normalY = -gy / (8.0 * 255.0);

                        // Map from [-1, 1] to [0, 255]
                        var r = (byte)Math.Clamp((normalX * 0.5 + 0.5) * 255, 0, 255);
                        var g = (byte)Math.Clamp((normalY * 0.5 + 0.5) * 255, 0, 255);

                        expandedNormals[x, y] = (r, g);
                    }
                }

                // NOW crop out the center tile
                var generatedNormals = new (byte r, byte g)[width, height];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var srcX = width + x;  // Center tile X
                        var srcY = height + y; // Center tile Y
                        generatedNormals[x, y] = expandedNormals[srcX, srcY];
                    }
                }

                // Calculate original normal map intensity (average deviation from 128)
                double originalIntensitySum = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = normalmapBmp.GetPixel(x, y);
                        var deviationR = Math.Abs(pixel.R - 128);
                        var deviationG = Math.Abs(pixel.G - 128);
                        originalIntensitySum += (deviationR + deviationG) / 2.0;
                    }
                }
                double originalIntensity = originalIntensitySum / (width * height);

                // Blend generated normal map with original using a blend of overlay and regular blend methods
                var blendedNormals = new (byte r, byte g)[width, height];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var origColor = normalmapBmp.GetPixel(x, y);
                        var (newR, newG) = generatedNormals[x, y];

                        // Apply alpha blend first to the detail layer
                        var detailR = (alpha * newR + (255 - alpha) * 128) / 255.0;
                        var detailG = (alpha * newG + (255 - alpha) * 128) / 255.0;

                        // Regular/Linear blend (33%)
                        var linearR = (origColor.R + detailR) / 2.0;
                        var linearG = (origColor.G + detailG) / 2.0;

                        // Overlay blend mode (67%)
                        double overlayR, overlayG;

                        // R
                        if (origColor.R < 128)
                            overlayR = (2.0 * origColor.R * detailR) / 255.0;
                        else
                            overlayR = 255.0 - (2.0 * (255.0 - origColor.R) * (255.0 - detailR)) / 255.0;

                        // G
                        if (origColor.G < 128)
                            overlayG = (2.0 * origColor.G * detailG) / 255.0;
                        else
                            overlayG = 255.0 - (2.0 * (255.0 - origColor.G) * (255.0 - detailG)) / 255.0;

                        // DC about B

                        // Combine: 33% linear + 67% overlay
                        var finalR = 0.33 * linearR + 0.67 * overlayR;
                        var finalG = 0.33 * linearG + 0.67 * overlayG;

                        blendedNormals[x, y] = ((byte)Math.Clamp(finalR, 0, 255), (byte)Math.Clamp(finalG, 0, 255));
                    }
                }

                // Calculate blended normal map intensity
                double blendedIntensitySum = 0;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var (r, g) = blendedNormals[x, y];
                        var deviationR = Math.Abs(r - 128);
                        var deviationG = Math.Abs(g - 128);
                        blendedIntensitySum += (deviationR + deviationG) / 2.0;
                    }
                }
                double blendedIntensity = blendedIntensitySum / (width * height);

                // Calculate intensity change ratio
                double intensityRatio = originalIntensity > 0 ? originalIntensity / blendedIntensity : 1.0;

                // Normalize back to original intensity
                var wroteBack = false;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var origColor = normalmapBmp.GetPixel(x, y);
                        var (blendedR, blendedG) = blendedNormals[x, y];

                        // Scale back to original intensity
                        var adjustedR = 128 + (blendedR - 128) * intensityRatio;
                        var adjustedG = 128 + (blendedG - 128) * intensityRatio;

                        var finalR = (byte)Math.Clamp(adjustedR, 0, 255);
                        var finalG = (byte)Math.Clamp(adjustedG, 0, 255);

                        if (finalR != origColor.R || finalG != origColor.G)
                        {
                            wroteBack = true;
                            var newColor = Color.FromArgb(origColor.A, finalR, finalG, 255);
                            normalmapBmp.SetPixel(x, y, newColor);
                        }
                    }
                }

                if (wroteBack)
                {
                    WriteImageAsTGA(normalmapBmp, normalmapFile);
                    Trace.WriteLine($"{pack.Name}: updated normal map in {Path.GetFileName(normalmapFile)}.");
                }
                else
                {
                    Trace.WriteLine($"{pack.Name}: no normal map changes in {Path.GetFileName(normalmapFile)}.");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"{pack.Name}: error processing {Path.GetFileName(normalmapFile)} — {ex.Message}");
            }
        }


        // helpers
        Color[,] ApplyEdgePadding(Bitmap bitmap)
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var result = new Color[width, height];

            // First pass: copy all pixels and mark opaque ones
            var isOpaque = new bool[width, height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    result[x, y] = pixel;
                    isOpaque[x, y] = pixel.A > 0;
                }
            }

            // Multi-pass flood fill: continue until no more transparent pixels are filled
            bool anyChanged;
            int maxPasses = width * height; // theoretical limit not sure if it is factual but at least there is something to prevent endless loops

            for (int pass = 0; pass < maxPasses; pass++)
            {
                anyChanged = false;
                var newOpaque = new bool[width, height];
                Array.Copy(isOpaque, newOpaque, isOpaque.Length);

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (isOpaque[x, y]) continue; // Already opaque

                        // Check all 4 directions for nearest opaque neighbor
                        Color? nearestColor = null;

                        // Check left
                        if (x > 0 && isOpaque[x - 1, y])
                            nearestColor = result[x - 1, y];
                        // Check right
                        else if (x < width - 1 && isOpaque[x + 1, y])
                            nearestColor = result[x + 1, y];
                        // Check top
                        else if (y > 0 && isOpaque[x, y - 1])
                            nearestColor = result[x, y - 1];
                        // Check bottom
                        else if (y < height - 1 && isOpaque[x, y + 1])
                            nearestColor = result[x, y + 1];

                        if (nearestColor.HasValue)
                        {
                            result[x, y] = Color.FromArgb(255, nearestColor.Value.R, nearestColor.Value.G, nearestColor.Value.B);
                            newOpaque[x, y] = true;
                            anyChanged = true;
                        }
                    }
                }

                isOpaque = newOpaque;

                if (!anyChanged) break; // All reachable transparent pixels have been filled
            }

            return result;
        }
    }



    private static void ProcessRoughness(PackInfo pack)
    {
        const double MetalnessModificationFraction = 0.33;
        const double MetalnessInfluenceOnRoughnessReduction = 0.33;
        const double BasePower = 2.2;
        const double ImpactMultiplier = 2.4;
        const double HighControlScaling = 8.0;

        if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
            return;

        var files = TextureSetHelper.RetrieveFilesFromTextureSets(pack.Path, TextureSetHelper.TextureType.Mer);
        if (!files.Any())
        {
            MainWindow.Log($"{pack.Name}: no MERS texture files found from texture sets.");
            return;
        }

        var controlValue = RoughnessControlValue;
        if (controlValue == 0)
            return;

        bool isIncreasingRoughness = controlValue > 0;
        int absControl = Math.Abs(controlValue);

        foreach (var file in files)
        {
            try
            {
                using var bmp = ReadImage(file, false);
                var width = bmp.Width;
                var height = bmp.Height;
                var wroteBack = false;

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var origColor = bmp.GetPixel(x, y);
                        int origRoughness = origColor.B;
                        int origMetalness = origColor.R;

                        int newRoughness, newMetalness;

                        if (isIncreasingRoughness)
                        {
                            // Increasing roughness: strong boost for low values, weak for high values
                            var normalized = origRoughness / 255.0;

                            // Curve power increases with control value for more aggression
                            var curveAggression = BasePower + (absControl / 25.0) * 1.5;

                            var inverseFactor = 1.0 - Math.Pow(normalized, curveAggression);

                            var maxBoost = absControl * ImpactMultiplier + (absControl / 12.0) * HighControlScaling;
                            var boost = maxBoost * inverseFactor;

                            newRoughness = (int)Math.Floor(origRoughness + boost);
                            newRoughness = Math.Clamp(newRoughness, 0, 255);

                            // Reduce metalness if pixel has metalness
                            if (origMetalness > 0)
                            {
                                var roughnessChange = newRoughness - origRoughness;
                                var metalnessReduction = roughnessChange * MetalnessModificationFraction;
                                newMetalness = (int)Math.Floor(origMetalness - metalnessReduction);
                                newMetalness = Math.Clamp(newMetalness, 0, 255);
                            }
                            else
                            {
                                newMetalness = origMetalness;
                            }
                        }
                        else // Decreasing roughness
                        {
                            // For high-metalness pixels, we want to reduce roughness more aggressively
                            // Use metalness as a multiplier for the reduction strength
                            var metalnessInfluence = origMetalness > 0 ? (origMetalness / 255.0) : 0.0;

                            // Base reduction using roughness curve
                            var roughnessNormalized = origRoughness / 255.0;
                            var curveAggression = BasePower + (absControl / 5.0) * 1.5;
                            var factor = Math.Pow(roughnessNormalized, curveAggression);

                            var maxReduction = absControl * ImpactMultiplier + (absControl / 5.0) * HighControlScaling;

                            // Blend between base reduction and metalness-influenced reduction
                            // High metalness = more aggressive reduction even for low roughness values
                            var baseReduction = maxReduction * factor;
                            var metalnessBonus = maxReduction * metalnessInfluence * MetalnessInfluenceOnRoughnessReduction;
                            var reduction = baseReduction + metalnessBonus;

                            newRoughness = (int)Math.Ceiling(origRoughness - reduction);
                            newRoughness = Math.Clamp(newRoughness, 0, 255);

                            // Increase metalness based on what roughness WOULD have gained if control was positive
                            if (origMetalness > 0)
                            {
                                // Calculate hypothetical boost using the POSITIVE curve logic
                                var positiveCurveAggression = BasePower + (absControl / 25.0) * 1.5;
                                var inverseFactor = 1.0 - Math.Pow(roughnessNormalized, positiveCurveAggression);
                                var hypotheticalMaxBoost = absControl * ImpactMultiplier + (absControl / 12.0) * HighControlScaling;
                                var hypotheticalBoost = hypotheticalMaxBoost * inverseFactor;

                                // Use that to boost metalness
                                var metalnessIncrease = hypotheticalBoost * MetalnessModificationFraction;
                                newMetalness = (int)Math.Ceiling(origMetalness + metalnessIncrease);
                                newMetalness = Math.Clamp(newMetalness, 0, 255);
                            }
                            else
                            {
                                newMetalness = origMetalness;
                            }
                        }

                        if (newRoughness != origRoughness || newMetalness != origMetalness)
                        {
                            wroteBack = true;
                            var newColor = Color.FromArgb(origColor.A, newMetalness, origColor.G, newRoughness);
                            bmp.SetPixel(x, y, newColor);
                        }
                    }
                }

                if (wroteBack)
                {
                    WriteImageAsTGA(bmp, file);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"{pack.Name}: error processing {Path.GetFileName(file)} — {ex.Message}", MainWindow.LogLevel.Error);
            }
        }
    }


    // TODO: Any other potential variant suffixes?!
    private static void ProcessMaterialGrain(PackInfo pack)
    {
        const double CHECKERBOARD_INTENSITY = 0.2; // % checkerboard pattern blend
        const double CHECKERBOARD_NOISE_AMOUNT = 0.2; // % noise on the checkerboard itself

        double CalculateEffectiveness(int colorValue)
        {
            if (colorValue == 128)
                return 1.0; // 100% effectiveness at 128

            if (colorValue < 128)
            {
                // Linear fall-off from 128 to 0: 100% at 128, 0% at 0
                return colorValue / 128.0;
            }
            else
            {
                // Linear fall-off from 128 to 255: 100% at 128, 33% at 255
                return 1.0 - (colorValue - 128) * 0.67 / 127.0;
            }
        }

        string GetBaseFilename(string filePath)
        {
            var filename = Path.GetFileNameWithoutExtension(filePath);

            // Define all known variant suffixes (case insensitive)
            var variantSuffixes = new[]
            {
            "_on", "_off", "_active", "_inactive", "_dormant", "_bloom", "_ejecting",
            "_lit", "_unlit", "_powered", "_crafting"
        };

            // Split by underscores and rebuild without any variant suffixes
            var parts = filename.Split('_');
            var baseParts = new List<string>();

            foreach (var part in parts)
            {
                var isVariantSuffix = false;
                foreach (var suffix in variantSuffixes)
                {
                    // Remove the leading underscore from suffix for comparison
                    var suffixWithoutUnderscore = suffix.Substring(1);
                    if (part.Equals(suffixWithoutUnderscore, StringComparison.OrdinalIgnoreCase))
                    {
                        isVariantSuffix = true;
                        break;
                    }
                }

                if (!isVariantSuffix)
                {
                    baseParts.Add(part);
                }
            }

            return string.Join("_", baseParts);
        }

        if (string.IsNullOrEmpty(pack.Path) || !Directory.Exists(pack.Path))
            return;

        var files = TextureSetHelper.RetrieveFilesFromTextureSets(pack.Path, TextureSetHelper.TextureType.Mer);

        if (!files.Any())
        {
            MainWindow.Log($"{pack.Name}: no MER(S) texture files found from texture sets.");
            return;
        }

        var materialNoiseOffset = MaterialNoiseOffset;
        if (materialNoiseOffset <= 0)
            return;

        var random = new Random();

        // Cache for shared noise patterns between variants
        var noisePatternCache = new Dictionary<string, (int[,] red, int[,] green, int[,] blue, int[,] checkerboard)>();

        // Group files by their base filename to identify variant families
        var fileGroups = files.GroupBy(file => GetBaseFilename(file))
                              .ToDictionary(g => g.Key, g => g.ToList());

        // Track processed files to avoid double-processing
        var processedFiles = new HashSet<string>();

        foreach (var fileGroup in fileGroups)
        {
            var baseFilename = fileGroup.Key;
            var variantFiles = fileGroup.Value;
            var hasMultipleVariants = variantFiles.Count > 1;

            foreach (var file in variantFiles)
            {
                if (processedFiles.Contains(file))
                    continue;

                processedFiles.Add(file);

                try
                {
                    using var bmp = ReadImage(file, false);
                    var width = bmp.Width;
                    var height = bmp.Height;

                    if (width == 0)
                        continue; // Skip if width is 0

                    // Check if this is an animated texture (flipbook)
                    var isAnimated = false;
                    var frameHeight = width; // First frame is always square
                    var frameCount = 1;

                    if (height >= width * 2 && height % width == 0)
                    {
                        frameCount = height / width;
                        isAnimated = frameCount >= 2;
                    }

                    // Determine cache key - try to match with variants of same dimensions
                    string cacheKey = $"{baseFilename}_{width}x{frameHeight}";

                    // Check if dimensions match any cached pattern for this base
                    bool dimensionsMatch = false;
                    if (hasMultipleVariants)
                    {
                        dimensionsMatch = noisePatternCache.ContainsKey(cacheKey);
                    }

                    // Get or generate noise pattern
                    int[,] redOffsets;
                    int[,] greenOffsets;
                    int[,] blueOffsets;
                    int[,] checkerboardOffsets;

                    if (dimensionsMatch && noisePatternCache.TryGetValue(cacheKey, out var cachedPattern))
                    {
                        // Use cached pattern (shared with variants)
                        redOffsets = cachedPattern.red;
                        greenOffsets = cachedPattern.green;
                        blueOffsets = cachedPattern.blue;
                        checkerboardOffsets = cachedPattern.checkerboard;
                    }
                    else
                    {
                        // Generate new noise pattern
                        redOffsets = new int[width, frameHeight];
                        greenOffsets = new int[width, frameHeight];
                        blueOffsets = new int[width, frameHeight];
                        checkerboardOffsets = new int[width, frameHeight];

                        // Pre-generate noise pattern for the frame dimensions
                        for (var y = 0; y < frameHeight; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                // Generate random noise offsets
                                redOffsets[x, y] = random.Next(-materialNoiseOffset, materialNoiseOffset + 1);
                                greenOffsets[x, y] = random.Next(-materialNoiseOffset, materialNoiseOffset + 1);
                                blueOffsets[x, y] = random.Next(-materialNoiseOffset, materialNoiseOffset + 1);

                                // Generate checkerboard pattern with noise
                                int baseCheckerboard = ((x + y) % 2) * 255; // 0 or 255
                                int checkerNoise = random.Next(
                                    (int)(-materialNoiseOffset * CHECKERBOARD_NOISE_AMOUNT),
                                    (int)(materialNoiseOffset * CHECKERBOARD_NOISE_AMOUNT) + 1
                                );
                                checkerboardOffsets[x, y] = Math.Clamp(baseCheckerboard + checkerNoise, 0, 255);
                            }
                        }

                        // Cache it if this texture has variants or might have variants
                        if (hasMultipleVariants)
                        {
                            noisePatternCache[cacheKey] = (redOffsets, greenOffsets, blueOffsets, checkerboardOffsets);
                        }
                    }

                    var wroteBack = false;

                    // Process all frames (use same noise pattern for all frames)
                    for (var frame = 0; frame < frameCount; frame++)
                    {
                        var frameStartY = frame * frameHeight;

                        for (var y = 0; y < frameHeight; y++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                var actualY = frameStartY + y;
                                var origColor = bmp.GetPixel(x, actualY);
                                int r = origColor.R;
                                int g = origColor.G;
                                int b = origColor.B;

                                // Get cached noise offsets (same for all frames and variants)
                                var redNoise = redOffsets[x, y];
                                var greenNoise = greenOffsets[x, y];
                                var blueNoise = blueOffsets[x, y];
                                var checkerboard = checkerboardOffsets[x, y];

                                // Calculate checkerboard contribution (centered around 0)
                                var checkerValue = (checkerboard - 127.5) * (materialNoiseOffset / 127.5);

                                // Blend noise with checkerboard pattern for each channel
                                var redFinalNoise = redNoise * (1.0 - CHECKERBOARD_INTENSITY) +
                                                   checkerValue * CHECKERBOARD_INTENSITY;
                                var greenFinalNoise = greenNoise * (1.0 - CHECKERBOARD_INTENSITY) +
                                                     checkerValue * CHECKERBOARD_INTENSITY;
                                var blueFinalNoise = blueNoise * (1.0 - CHECKERBOARD_INTENSITY) +
                                                    checkerValue * CHECKERBOARD_INTENSITY;

                                // Calculate effectiveness based on current color values
                                var redEffectiveness = CalculateEffectiveness(r);
                                var greenEffectiveness = CalculateEffectiveness(g) * 0.2; // Keep green at 1/5 effectiveness
                                var blueEffectiveness = CalculateEffectiveness(b);

                                // Apply effectiveness to final noise offsets, rounded
                                var effectiveRedOffset = (int)Math.Round(redFinalNoise * redEffectiveness);
                                var effectiveGreenOffset = (int)Math.Round(greenFinalNoise * greenEffectiveness);
                                var effectiveBlueOffset = (int)Math.Round(blueFinalNoise * blueEffectiveness);

                                var newR = r + effectiveRedOffset;
                                var newG = g + effectiveGreenOffset;
                                var newB = b + effectiveBlueOffset;

                                // Anti-clipping rule: discard if would cause clipping, keep original colors!
                                if (newR < 0 || newR > 255) newR = r;
                                if (newG < 0 || newG > 255) newG = g;
                                if (newB < 0 || newB > 255) newB = b;

                                if (newR != r || newG != g || newB != b)
                                {
                                    wroteBack = true;
                                    var newColor = Color.FromArgb(origColor.A, newR, newG, newB);
                                    bmp.SetPixel(x, actualY, newColor);
                                }
                            }
                        }
                    }

                    if (wroteBack)
                    {
                        WriteImageAsTGA(bmp, file);
                        // MainWindow.Log($"{pack.Name}: added material noise to {Path.GetFileName(file)}.");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"{pack.Name}: error processing {Path.GetFileName(file)} — {ex.Message}", MainWindow.LogLevel.Error);
                }
            }
        }
    }


    #endregion Processors -------------------
}



public static class TextureSetHelper
{
    public enum TextureType
    {
        Color,
        Mer,        // metalness_emissive_roughness or metalness_emissive_roughness_subsurface
        Normal,
        Heightmap
    }

    private static readonly string[] SupportedExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// Retrieves paired texture file paths from texture set JSONs.
    /// </summary>
    /// <param name="rootPath">Folder to search for texture set JSONs.</param>
    /// <param name="primaryType">Primary texture type (e.g., Color).</param>
    /// <param name="secondaryType">Secondary texture type (e.g., Heightmap).</param>
    /// <returns>Array of texture pairs. Secondary can be null if not found.</returns>
    public static (string primary, string? secondary)[] RetrieveTextureSetPairs(string rootPath, TextureType primaryType, TextureType secondaryType)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            return Array.Empty<(string, string?)>();

        var jsonFiles = Directory.GetFiles(rootPath, "*.texture_set.json", SearchOption.AllDirectories);
        var foundPairs = new List<(string primary, string? secondary)>();

        foreach (var jsonFile in jsonFiles)
        {
            try
            {
                var text = File.ReadAllText(jsonFile);
                var root = JObject.Parse(text);
                if (root.SelectToken("minecraft:texture_set") is not JObject set)
                    continue;

                string? primaryTextureName = primaryType switch
                {
                    TextureType.Color => set.Value<string>("color"),
                    TextureType.Mer => set.Value<string>("metalness_emissive_roughness") ?? set.Value<string>("metalness_emissive_roughness_subsurface"),
                    TextureType.Normal => set.Value<string>("normal"),
                    TextureType.Heightmap => set.Value<string>("heightmap"),
                    _ => null
                };

                string? secondaryTextureName = secondaryType switch
                {
                    TextureType.Color => set.Value<string>("color"),
                    TextureType.Mer => set.Value<string>("metalness_emissive_roughness") ?? set.Value<string>("metalness_emissive_roughness_subsurface"),
                    TextureType.Normal => set.Value<string>("normal"),
                    TextureType.Heightmap => set.Value<string>("heightmap"),
                    _ => null
                };

                if (string.IsNullOrEmpty(primaryTextureName))
                    continue;

                var folder = Path.GetDirectoryName(jsonFile);
                var primaryFound = FindTextureFile(folder, primaryTextureName);

                if (!string.IsNullOrEmpty(primaryFound))
                {
                    string? secondaryFound = null;
                    if (!string.IsNullOrEmpty(secondaryTextureName))
                    {
                        secondaryFound = FindTextureFile(folder, secondaryTextureName);
                    }

                    foundPairs.Add((primaryFound, secondaryFound));
                }
            }
            catch
            {
                // Ignore malformed JSONs or IO errors
            }
        }

        return foundPairs.ToArray();
    }

    /// <summary>
    /// Retrieves texture file paths referenced by texture set JSONs in the given folder.
    /// </summary>
    /// <param name="rootPath">Folder to search for texture set JSONs.</param>
    /// <param name="type">Texture type to retrieve.</param>
    /// <returns>Array of found texture file paths (unique entries only).</returns>
    public static string[] RetrieveFilesFromTextureSets(string rootPath, TextureType type)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            return Array.Empty<string>();

        var jsonFiles = Directory.GetFiles(rootPath, "*.texture_set.json", SearchOption.AllDirectories);
        var foundFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var jsonFile in jsonFiles)
        {
            try
            {
                var text = File.ReadAllText(jsonFile);
                var root = JObject.Parse(text);
                if (root.SelectToken("minecraft:texture_set") is not JObject set)
                    continue;

                string? textureName = type switch
                {
                    TextureType.Color => set.Value<string>("color"),
                    TextureType.Mer => set.Value<string>("metalness_emissive_roughness") ?? set.Value<string>("metalness_emissive_roughness_subsurface"),
                    TextureType.Normal => set.Value<string>("normal"),
                    TextureType.Heightmap => set.Value<string>("heightmap"),
                    _ => null
                };

                if (string.IsNullOrEmpty(textureName))
                    continue;

                var folder = Path.GetDirectoryName(jsonFile);
                var found = FindTextureFile(folder, textureName);

                if (!string.IsNullOrEmpty(found))
                    foundFiles.Add(found);
            }
            catch
            {
                // Ignore malformed JSONs or IO errors
            }
        }

        return foundFiles.ToArray();
    }

    /// <summary>
    /// Finds a texture file with case-insensitive search, trying extensions in priority order.
    /// </summary>
    /// <param name="folder">Directory to search in.</param>
    /// <param name="textureName">Base texture name without extension.</param>
    /// <returns>Full path to found file, or null if not found.</returns>
    private static string FindTextureFile(string folder, string textureName)
    {
        foreach (var ext in SupportedExtensions)
        {
            var targetPath = Path.Combine(folder, textureName + ext);

            // Try exact case first (fastest)
            if (File.Exists(targetPath))
                return targetPath;

            // If exact case fails, do case-insensitive search
            try
            {
                var files = Directory.GetFiles(folder, textureName + ext, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                    return files[0];
            }
            catch
            {
                // Directory might not exist or access denied, continue to next extension
            }
        }

        return null;
    }
}
