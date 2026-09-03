using System.Text.RegularExpressions;
using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.WebAutomation.Browser;

public sealed class ZoomProfileManager
{
    private const string ReadyMarkerFileName = ".zoom-session-ready";
    private static readonly Regex SafeProfileNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _profilesRoot;

    public ZoomProfileManager(string? profilesRoot = null)
    {
        _profilesRoot = Path.GetFullPath(profilesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomAutoAdmit",
            "Profiles"));
    }

    public string ProfilesRoot => _profilesRoot;

    public ZoomBrowserProfile GetOrCreate(string requestedName)
    {
        string name = NormalizeAndValidateName(requestedName);
        Directory.CreateDirectory(_profilesRoot);
        string directory = Path.GetFullPath(Path.Combine(_profilesRoot, name));
        string rootWithSeparator = _profilesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved browser profile escaped the managed Profiles directory.");
        Directory.CreateDirectory(directory);

        // A per-session profile (name-2, name-3, ...) starts as a copy of the account's own
        // profile, so the extra simultaneous meeting is already signed in. Chromium cannot share
        // one user-data directory between two browsers, which is why the copy exists at all.
        if (Directory.GetFileSystemEntries(directory).Length == 0 &&
            Core.Sessions.AccountWebProfile.BaseProfileOf(name) is { } baseProfileName)
        {
            string baseDirectory = Path.Combine(_profilesRoot, baseProfileName);
            if (Directory.Exists(baseDirectory) && Directory.GetFileSystemEntries(baseDirectory).Length > 0)
            {
                try { CopySignedInProfile(baseDirectory, directory); }
                catch (Exception ex)
                {
                    ConsoleLogger.Warn(
                        $"[WEB_PROFILE] '{name}' could not be seeded from '{baseProfileName}'; it may need a manual sign-in: {ex.Message}");
                }
            }
        }

        // If directory is empty, check if an alias profile exists (e.g. CAI5_AIS4_S8 -> s8, S8)
        if (Directory.GetFileSystemEntries(directory).Length == 0)
        {
            var parts = name.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string lastPart = parts[^1];
                string[] candidateAliases = [lastPart.ToLowerInvariant(), lastPart.ToUpperInvariant(), lastPart];
                foreach (var alias in candidateAliases)
                {
                    string aliasDir = Path.Combine(_profilesRoot, alias);
                    if (Directory.Exists(aliasDir) && Directory.GetFileSystemEntries(aliasDir).Length > 0 && !aliasDir.Equals(directory, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            CopyDirectoryRecursively(aliasDir, directory);
                        }
                        catch { }
                        break;
                    }
                }
            }
        }

        string marker = Path.Combine(directory, ReadyMarkerFileName);
        return new ZoomBrowserProfile(name, directory, marker, File.Exists(marker));
    }

    // Chromium's own lock files must never be copied, and caches would only slow the copy down.
    private static readonly string[] SkippedProfileDirectories =
    [
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "GrShaderCache", "ShaderCache", "GraphiteDawnCache", "Crashpad", "component_crx_cache",
        "extensions_crx_cache", "Safe Browsing", "optimization_guide_model_store"
    ];

    private static void CopySignedInProfile(string sourceDir, string targetDir)
    {
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => SkippedProfileDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                continue;
            string fileName = segments[^1];
            if (fileName.StartsWith("Singleton", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("LOCK", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(ReadyMarkerFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            string destination = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // A file the live browser holds open is skipped rather than failing the whole copy.
            try { File.Copy(file, destination, overwrite: true); copied++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Carry the sign-in marker over only when the copy actually produced a profile.
        string sourceMarker = Path.Combine(sourceDir, ReadyMarkerFileName);
        if (copied > 0 && File.Exists(sourceMarker))
            File.Copy(sourceMarker, Path.Combine(targetDir, ReadyMarkerFileName), overwrite: true);
        ConsoleLogger.Info($"[WEB_PROFILE] Seeded a per-session browser profile with {copied} file(s) from '{Path.GetFileName(sourceDir)}'.");
    }

    private static void CopyDirectoryRecursively(string sourceDir, string targetDir)
    {
        foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dirPath.Replace(sourceDir, targetDir));
        }

        foreach (string newPath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
        {
            File.Copy(newPath, newPath.Replace(sourceDir, targetDir), true);
        }
    }

    public ZoomBrowserProfile MarkSessionReady(ZoomBrowserProfile profile)
    {
        ValidateManagedProfile(profile);
        File.WriteAllText(profile.ReadyMarkerPath, "Zoom authenticated meeting session initialized.");
        return profile with { HasReusableSession = true };
    }

    /// <summary>
    /// Drops the saved sign-in marker after Zoom stops accepting it, so the next launch opens a
    /// visible browser for a fresh sign-in instead of failing headlessly again.
    /// </summary>
    public ZoomBrowserProfile MarkSessionExpired(ZoomBrowserProfile profile)
    {
        ValidateManagedProfile(profile);
        try { if (File.Exists(profile.ReadyMarkerPath)) File.Delete(profile.ReadyMarkerPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return profile with { HasReusableSession = false };
    }

    public ZoomBrowserLaunchPlan CreateLaunchPlan(ZoomBrowserProfile profile, bool forceHeaded)
    {
        ValidateManagedProfile(profile);
        return new ZoomBrowserLaunchPlan(profile, Headless: profile.HasReusableSession && !forceHeaded);
    }

    private static string NormalizeAndValidateName(string requestedName)
    {
        string name = requestedName.Trim();
        if (name.Equals("default", StringComparison.OrdinalIgnoreCase)) name = "Default";
        if (!SafeProfileNamePattern.IsMatch(name))
            throw new ArgumentException(
                "Profile name must be 1-64 characters using letters, numbers, dot, underscore, or hyphen.",
                nameof(requestedName));
        return name;
    }

    private void ValidateManagedProfile(ZoomBrowserProfile profile)
    {
        string directory = Path.GetFullPath(profile.DirectoryPath);
        string rootWithSeparator = _profilesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Browser profile is outside the managed Profiles directory.");
    }
}
