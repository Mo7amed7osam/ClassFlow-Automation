using System.Text.Json;
using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>
/// The on/off switch for letting people in, and the tally of how many were let in today.
///
/// Both live in files rather than in memory because the desktop engine runs in its own process:
/// the window flips the switch, the engine reads it. Admitting is ON unless somebody turns it
/// off, so a missing or unreadable file always means "keep working" — a broken file must never
/// silently stop a meeting from admitting its students.
/// </summary>
public static class AdmissionControl
{
    private static readonly object Gate = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMilliseconds(750);

    private static bool _paused;
    private static DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private static bool _preferAdmitAll = true;
    private static bool _showWebBrowser = true;
    private static DateTimeOffset _preferencesReadAt = DateTimeOffset.MinValue;

    /// <summary>Where the switch and today's count are kept; tests point it at a folder of their own.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit");

    private static string SwitchPath => Path.Combine(Folder, "admission-switch.json");
    private static string PreferencesPath => Path.Combine(Folder, "admission-preferences.json");
    private static string TallyPath => Path.Combine(Folder, "admission-tally.json");

    private sealed record SwitchFile(bool Paused);
    private sealed record PreferenceFile(bool PreferAdmitAll, bool ShowWebBrowser = true);
    private sealed record TallyFile(string Date, int Count);

    /// <summary>False while admitting is paused. Re-read from disk at most once per 750 ms.</summary>
    public static bool IsAdmitting
    {
        get
        {
            lock (Gate)
            {
                if (DateTimeOffset.UtcNow - _readAt < CacheFor) return !_paused;
                _readAt = DateTimeOffset.UtcNow;
                try
                {
                    _paused = File.Exists(SwitchPath) &&
                              (JsonSerializer.Deserialize<SwitchFile>(File.ReadAllText(SwitchPath))?.Paused ?? false);
                }
                catch
                {
                    _paused = false;   // Unreadable means keep admitting.
                }
                return !_paused;
            }
        }
    }

    /// <summary>Turns admitting on or off for every engine, now and after a restart.</summary>
    public static void SetPaused(bool paused)
    {
        lock (Gate)
        {
            _paused = paused;
            _readAt = DateTimeOffset.UtcNow;
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(SwitchPath, JsonSerializer.Serialize(new SwitchFile(paused)));
                ConsoleLogger.Info($"[ADMISSION] Admitting {(paused ? "paused" : "resumed")} by the operator.");
            }
            catch (Exception ex)
            {
                ConsoleLogger.Warn($"[ADMISSION] The switch could not be saved; {ex.Message}");
            }
        }
    }

    private static bool _autoCoHost = true;
    private static DateTimeOffset _coHostReadAt = DateTimeOffset.MinValue;
    private static string CoHostSwitchPath => Path.Combine(Folder, "cohost-switch.json");
    private sealed record CoHostSwitchFile(bool On);

    /// <summary>
    /// False while the operator has turned automatic co-host off - to try something, or because
    /// they took co-host away on purpose. Nobody is then made co-host, or made co-host again, until
    /// it is switched back on. This PC only, like the admit switch; on unless turned off.
    /// </summary>
    public static bool IsAutoCoHost
    {
        get
        {
            lock (Gate)
            {
                if (DateTimeOffset.UtcNow - _coHostReadAt < CacheFor) return _autoCoHost;
                _coHostReadAt = DateTimeOffset.UtcNow;
                try
                {
                    _autoCoHost = !File.Exists(CoHostSwitchPath) ||
                                  (JsonSerializer.Deserialize<CoHostSwitchFile>(File.ReadAllText(CoHostSwitchPath))?.On ?? true);
                }
                catch { _autoCoHost = true; }
                return _autoCoHost;
            }
        }
    }

    public static void SetAutoCoHost(bool on)
    {
        lock (Gate)
        {
            _autoCoHost = on;
            _coHostReadAt = DateTimeOffset.UtcNow;
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(CoHostSwitchPath, JsonSerializer.Serialize(new CoHostSwitchFile(on)));
                ConsoleLogger.Info($"[COHOST] Automatic co-host turned {(on ? "on" : "off")} by the operator.");
            }
            catch (Exception ex)
            {
                ConsoleLogger.Warn($"[COHOST] The co-host switch could not be saved; {ex.Message}");
            }
        }
    }

    /// <summary>
    /// True while one "Admit all" press is preferred over admitting people one at a time.
    /// Preferred unless somebody turns it off, and read from disk so the engine in its own
    /// process follows the same choice.
    /// </summary>
    public static bool PrefersAdmitAll
    {
        get
        {
            lock (Gate)
            {
                if (DateTimeOffset.UtcNow - _preferencesReadAt < CacheFor) return _preferAdmitAll;
                _preferencesReadAt = DateTimeOffset.UtcNow;
                ReadPreferences();
                return _preferAdmitAll;
            }
        }
    }

    /// <summary>Chooses between one "Admit all" press and admitting people one at a time.</summary>
    public static void SetPrefersAdmitAll(bool preferAdmitAll)
    {
        lock (Gate)
        {
            ReadPreferences(force: true);
            _preferAdmitAll = preferAdmitAll;
            WritePreferences($"'Admit all' is now {(preferAdmitAll ? "preferred" : "not used")}.");
        }
    }

    /// <summary>
    /// True while a Web meeting opens a browser window you can watch. Turning it off runs the
    /// same meeting headlessly: nothing appears on screen, which suits a scheduled run nobody is
    /// sitting in front of.
    /// </summary>
    public static bool ShowWebBrowser
    {
        get { lock (Gate) { ReadPreferences(); return _showWebBrowser; } }
    }

    /// <summary>Shows or hides the browser the Web engine drives.</summary>
    public static void SetShowWebBrowser(bool showWebBrowser)
    {
        lock (Gate)
        {
            ReadPreferences(force: true);
            _showWebBrowser = showWebBrowser;
            WritePreferences($"Web meetings will {(showWebBrowser ? "open a visible browser" : "run without a visible browser")}.");
        }
    }

    private static void ReadPreferences(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _preferencesReadAt < CacheFor) return;
        _preferencesReadAt = DateTimeOffset.UtcNow;
        try
        {
            var stored = File.Exists(PreferencesPath)
                ? JsonSerializer.Deserialize<PreferenceFile>(File.ReadAllText(PreferencesPath))
                : null;
            _preferAdmitAll = stored?.PreferAdmitAll ?? true;
            _showWebBrowser = stored?.ShowWebBrowser ?? true;
        }
        catch
        {
            // An unreadable file keeps the defaults rather than changing how meetings behave.
            _preferAdmitAll = true;
            _showWebBrowser = true;
        }
    }

    private static void WritePreferences(string what)
    {
        _preferencesReadAt = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(
                PreferencesPath,
                JsonSerializer.Serialize(new PreferenceFile(_preferAdmitAll, _showWebBrowser)));
            ConsoleLogger.Info($"[ADMISSION] {what}");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[ADMISSION] The preference could not be saved; {ex.Message}");
        }
    }

    /// <summary>How many people were admitted today. Yesterday's tally reads as zero.</summary>
    public static int AdmittedToday()
    {
        lock (Gate) { return ReadTally().Count; }
    }

    /// <summary>Counts one verified admission against today.</summary>
    public static void RecordAdmission()
    {
        lock (Gate)
        {
            var tally = ReadTally();
            Write(tally with { Count = tally.Count + 1 });
        }
    }

    /// <summary>Sets today's tally back to zero. Only the person watching the meeting does this.</summary>
    public static void ResetToday()
    {
        lock (Gate) { Write(new TallyFile(Today(), 0)); }
    }

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    private static TallyFile ReadTally()
    {
        try
        {
            if (File.Exists(TallyPath) &&
                JsonSerializer.Deserialize<TallyFile>(File.ReadAllText(TallyPath)) is { } stored &&
                stored.Date == Today())
                return stored;
        }
        catch { }
        // A new day, a missing file or a damaged one all start from zero rather than reporting
        // a count that would be wrong.
        return new TallyFile(Today(), 0);
    }

    private static void Write(TallyFile tally)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(TallyPath, JsonSerializer.Serialize(tally));
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[ADMISSION] The tally could not be saved; {ex.Message}");
        }
    }
}
