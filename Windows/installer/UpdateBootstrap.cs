// The small update an older copy of the app downloads instead of the 170 MB installer.
//
// Copies of the app from before changed-files updates only know one way to update: download the
// "installer" the server names and run it with --update <folder>. publish-update.ps1 compiles this
// file into that "installer" with the Windows' own C# compiler (.NET Framework 4, present on every
// Windows 10/11, so nothing has to be installed), carrying only the app's own files, compressed.
//
// It waits for the app to close, builds the new version beside the installed one from the files
// already there plus the ones it carries, checks every file against the new version's list
// (SHA-256), swaps the folders and opens the app again. If any file does not match - an install too
// old to update this way - nothing is changed and the old app is opened again.
//
// C# 5 only: that is the language the Windows compiler understands.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

internal static class UpdateBootstrap
{
    private const string Exe = "ZoomAutoAdmit.WindowsUI.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZoomAutoAdmit";

    private static int Main(string[] args)
    {
        string folder = null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--update") folder = args[i + 1];
        if (folder == null) { Log("started without --update <folder>; nothing to do"); return 2; }
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        string version = typeof(UpdateBootstrap).Assembly.GetName().Version.ToString();
        try
        {
            Log("update to " + version + " (small update) in " + folder);
            if (!File.Exists(Path.Combine(folder, Exe))) { Log("no app in " + folder); return 3; }
            WaitAndClose(folder);

            string staged = folder + ".update", previous = folder + ".previous";
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(staged);
            if (!Build(folder, staged))
            {
                TryDelete(staged);
                Log("this install is too old for a small update; nothing was changed");
                Start(folder);
                return 4;
            }

            if (Directory.Exists(previous)) TryDelete(previous);
            try { MoveWithRetry(folder, previous); }
            catch (Exception ex) { Log("the app folder could not be moved, nothing was changed: " + ex.Message); TryDelete(staged); Start(folder); return 5; }
            try { MoveWithRetry(staged, folder); }
            catch (Exception ex)
            {
                Log("the new version could not take its place; putting the old one back: " + ex.Message);
                try { MoveWithRetry(previous, folder); } catch (Exception back) { Log("could not put it back: " + back.Message); }
                Start(folder);
                return 6;
            }
            try { using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey)) key.SetValue("DisplayVersion", version); } catch { }
            Start(folder);
            TryDelete(previous);
            Log("update finished");
            return 0;
        }
        catch (Exception ex)
        {
            Log("the update failed: " + ex.Message);
            try { if (File.Exists(Path.Combine(folder, Exe))) Start(folder); } catch { }
            return 1;
        }
    }

    /// <summary>The new version in <paramref name="staged"/>: carried files unpacked, the rest copied, every one checked.</summary>
    private static bool Build(string folder, string staged)
    {
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            var carried = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry list = null;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == "files.tsv") list = entry;
                else carried[entry.FullName.Replace('/', '\\')] = entry;
            }
            if (list == null) { Log("the update carries no file list"); return false; }

            string root = Path.GetFullPath(staged).TrimEnd('\\') + "\\";
            using (var reader = new StreamReader(list.Open()))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length != 3) continue;
                    string path = parts[0], sha = parts[1];
                    long size = long.Parse(parts[2]);
                    string target = Path.GetFullPath(Path.Combine(staged, path));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Log("a path outside the app: " + path); return false; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    ZipArchiveEntry packed;
                    if (carried.TryGetValue(path, out packed))
                    {
                        using (var input = packed.Open())
                        using (var output = File.Create(target))
                            input.CopyTo(output);
                    }
                    else
                    {
                        string here = Path.Combine(folder, path);
                        if (!File.Exists(here)) { Log("missing here: " + path); return false; }
                        File.Copy(here, target, true);
                    }
                    if (new FileInfo(target).Length != size || !string.Equals(Hash(target), sha, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("does not match the new version: " + path);
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static void WaitAndClose(string folder)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && RunningFrom(folder, Exe).Count > 0) Thread.Sleep(500);
        for (int round = 0; round < 3; round++)
        {
            var left = RunningFrom(folder, null);
            if (left.Count == 0) break;
            foreach (var process in left)
            {
                Log("closing " + process.ProcessName + " (" + process.Id + ")");
                try { Process.Start(new ProcessStartInfo("taskkill.exe", "/F /T /PID " + process.Id) { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(10000); } catch { }
            }
            Thread.Sleep(1000);
        }
    }

    private static List<Process> RunningFrom(string folder, string exeName)
    {
        string root = folder.TrimEnd('\\') + "\\";
        var found = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string file = process.MainModule.FileName;
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (exeName != null && !Path.GetFileName(file).Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(process);
            }
            catch { }
        }
        return found;
    }

    private static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) { if (attempt >= 30) throw; Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { if (attempt >= 30) throw; Thread.Sleep(500); }
        }
    }

    private static void Start(string folder)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(folder, Exe)) { UseShellExecute = true, WorkingDirectory = folder }); }
        catch (Exception ex) { Log("could not open the app: " + ex.Message); }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) { Log("left behind " + folder + ": " + ex.Message); }
    }

    private static void Log(string line)
    {
        try
        {
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Logs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "update.log"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + line + Environment.NewLine);
        }
        catch { }
    }
}
