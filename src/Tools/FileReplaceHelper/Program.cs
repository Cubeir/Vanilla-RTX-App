// FileReplaceHelper - a small, single-purpose elevated helper.
//
// Launched on demand by the main app (which never elevates itself) whenever a feature needs
// to write into a folder that requires administrator rights - Minecraft's install directory
// under Program Files, mainly. app.manifest requests requireAdministrator, so Windows shows
// the standard "Do you want to allow this app to make changes" UAC prompt the moment this
// process is launched. The main app itself is never elevated and never restarts - see
// Helpers.ReplaceFilesWithElevation for the full rationale (this replaced an approach that
// wrote a temp .bat file and elevated cmd.exe against it, which is exactly the "drop a
// script and immediately run it elevated" shape some AVs flag on stricter systems).
//
// Arguments are source/destination path PAIRS taken directly off the command line - no temp
// file, no shell, no script interpretation of any kind. Each pair is a plain File.Copy.
//
// Exit code 0 = every file copied successfully. Any other code = at least one failed; details
// go to stderr for anyone running it manually while debugging.

using System.IO;

if (args.Length == 0 || args.Length % 2 != 0)
{
    Console.Error.WriteLine("Usage: FileReplaceHelper.exe <source1> <dest1> [<source2> <dest2> ...]");
    return 2;
}

var failed = false;

for (var i = 0; i < args.Length; i += 2)
{
    var source = args[i];
    var dest = args[i + 1];

    try
    {
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        File.Copy(source, dest, overwrite: true);
    }
    catch (Exception ex)
    {
        failed = true;
        Console.Error.WriteLine($"Failed to copy \"{source}\" -> \"{dest}\": {ex.Message}");
    }
}

return failed ? 1 : 0;
