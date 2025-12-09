using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Modules;
public class Launcher
{
    public static async Task<string> LaunchMinecraftRTXAsync(bool isTargetingPreview)
    {
        try
        {
            string basePath, protocol, versionName;

            if (isTargetingPreview)
            {
                basePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Minecraft Bedrock Preview"
                );
                protocol = "minecraft-preview://";
                versionName = "Minecraft Preview";
            }
            else
            {
                basePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Minecraft Bedrock"
                );
                protocol = "minecraft://";
                versionName = "Minecraft";
            }

            var usersPath = Path.Combine(basePath, "Users");

            if (!Directory.Exists(usersPath))
            {
                return $"❗ {versionName} users directory not found at: {usersPath}\n" +
                       "Make sure the correct version of the game is installed and has been launched at least once.";
            }

            // Find all options.txt files recursively
            var optionsFiles = Directory.GetFiles(usersPath, "options.txt", SearchOption.AllDirectories);

            if (optionsFiles.Length == 0)
            {
                return $"❗ No options.txt files found for {versionName}.\n" +
                       "Make sure the game has been launched at least once.";
            }

            var allStatusMessages = new List<string>();
            var filesProcessed = 0;
            var anyModificationsMade = false;

            // Process each options file
            foreach (var optionsFilePath in optionsFiles)
            {
                var fileStatusMessages = new List<string>();
                var relativePath = Path.GetRelativePath(usersPath, optionsFilePath);
                var userFolder = relativePath.Split(Path.DirectorySeparatorChar)[0]; // First folder after Users\

                // Check file accessibility
                try
                {
                    using var fileStream = File.Open(optionsFilePath, FileMode.Open, FileAccess.ReadWrite);
                }
                catch (UnauthorizedAccessException)
                {
                    fileStatusMessages.Add($"❗ Access denied to [{userFolder}] options file");
                    allStatusMessages.AddRange(fileStatusMessages);
                    continue;
                }
                catch (IOException ex)
                {
                    fileStatusMessages.Add($"❗ File inaccessible [{userFolder}]: {ex.Message}");
                    allStatusMessages.AddRange(fileStatusMessages);
                    continue;
                }

                // Remove read-only attribute
                try
                {
                    var fileInfo = new FileInfo(optionsFilePath);
                    if (fileInfo.IsReadOnly)
                    {
                        fileInfo.IsReadOnly = false;
                    }
                }
                catch (Exception ex)
                {
                    fileStatusMessages.Add($"❗ Failed to remove readonly attribute [{userFolder}]: {ex.Message}");
                    allStatusMessages.AddRange(fileStatusMessages);
                    continue;
                }

                try
                {
                    var lines = await File.ReadAllLinesAsync(optionsFilePath);
                    var fileModified = false;

                    // Update graphics_mode
                    var graphicsModeFound = false;
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("graphics_mode:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = lines[i].Split(':');
                            if (parts.Length > 1)
                            {
                                var oldValue = parts[1].Trim();
                                if (oldValue != "3")
                                {
                                    lines[i] = "graphics_mode:3";
                                    fileStatusMessages.Add($"[{userFolder}] graphics_mode: {oldValue} -> 3");
                                    fileModified = true;
                                }
                            }
                            graphicsModeFound = true;
                            break;
                        }
                    }
                    if (!graphicsModeFound)
                    {
                        var linesList = lines.ToList();
                        linesList.Add("graphics_mode:3");
                        lines = linesList.ToArray();
                        fileStatusMessages.Add($"[{userFolder}] Added graphics_mode:3");
                        fileModified = true;
                    }

                    // Update graphics_mode_switch
                    var graphicsModeSwitchFound = false;
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("graphics_mode_switch:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = lines[i].Split(':');
                            if (parts.Length > 1)
                            {
                                var oldValue = parts[1].Trim();
                                if (oldValue != "1")
                                {
                                    lines[i] = "graphics_mode_switch:1";
                                    fileStatusMessages.Add($"[{userFolder}] graphics_mode_switch: {oldValue} -> 1");
                                    fileModified = true;
                                }
                            }
                            graphicsModeSwitchFound = true;
                            break;
                        }
                    }
                    if (!graphicsModeSwitchFound)
                    {
                        var linesList = lines.ToList();
                        linesList.Add("graphics_mode_switch:1");
                        lines = linesList.ToArray();
                        fileStatusMessages.Add($"[{userFolder}] Added graphics_mode_switch:1");
                        fileModified = true;
                    }

                    // Update VSync
                    var vsyncFound = false;
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("gfx_vsync:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = lines[i].Split(':');
                            if (parts.Length > 1)
                            {
                                var oldValue = parts[1].Trim();
                                if (oldValue != "0")
                                {
                                    lines[i] = "gfx_vsync:0";
                                    fileStatusMessages.Add($"[{userFolder}] gfx_vsync: {oldValue} -> 0");
                                    fileModified = true;
                                }
                            }
                            vsyncFound = true;
                            break;
                        }
                    }
                    if (!vsyncFound)
                    {
                        var linesList = lines.ToList();
                        linesList.Add("gfx_vsync:0");
                        lines = linesList.ToArray();
                        fileStatusMessages.Add($"[{userFolder}] Added gfx_vsync:0");
                        fileModified = true;
                    }

                    // Always create backup and write file
                    var backupPath = optionsFilePath + ".backup";
                    File.Copy(optionsFilePath, backupPath, true);

                    await File.WriteAllLinesAsync(optionsFilePath, lines);

                    if (fileModified)
                    {
                        anyModificationsMade = true;
                    }

                    filesProcessed++;
                }
                catch (Exception ex)
                {
                    fileStatusMessages.Add($"❗ Failed to update [{userFolder}] options file: {ex.Message}");
                }

                // Add file-specific messages to overall status
                allStatusMessages.AddRange(fileStatusMessages);
            }

            if (filesProcessed == 0)
            {
                return "❗ No options files could be processed due to access issues.";
            }

            allStatusMessages.Add($"Processed {filesProcessed} options file(s).");

            // Launch the game
            await Task.Delay(250);

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = protocol,
                    UseShellExecute = true,
                    ErrorDialog = false
                };

                Process.Start(processInfo);
                allStatusMessages.Add($"✅ Ray tracing settings updated and launched {versionName} successfully.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                allStatusMessages.Add($"Failed to launch {versionName}: {ex.Message}");
                if (anyModificationsMade)
                    allStatusMessages.Add("⚠️ Ray tracing settings were updated successfully — you should now launch the game manually.");
            }
            catch (Exception ex)
            {
                allStatusMessages.Add($"Unexpected error launching {versionName}: {ex.Message}");
                if (anyModificationsMade)
                    allStatusMessages.Add("⚠️ Ray tracing settings were updated successfully — you should now launch the game manually.");
            }

            return string.Join("\n", allStatusMessages);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}