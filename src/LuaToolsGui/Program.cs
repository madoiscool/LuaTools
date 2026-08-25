using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using Velopack;

namespace LuaToolsGui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handle SAM worker CLI invocation before any WPF or single-instance mutex
        if (args is { Length: > 0 } && args[0].Equals("--sam-worker", StringComparison.OrdinalIgnoreCase))
        {
            int exitCode = Services.SAM.SamWorker.RunAsync(args).GetAwaiter().GetResult();
            Environment.Exit(exitCode);
            return;
        }

        // MUST run before any WPF/UI work: handles Velopack install/update hooks,
        // then no-ops on a normal launch.
        VelopackApp.Build().Run();

        // Set the UI culture before any WPF element is created, so x:Static resource lookups (which
        // resolve once at parse time) pick up the right language from the first frame.
        ApplyUiCulture();

        // Register the luatools:// protocol handler so browser links can open the app.
        Services.ProtocolService.Register();

        // Check if we were launched with a luatools:// protocol URL.
        string? protocolUrl = null;
        bool startMinimized = false;
        bool trayLocked = false;
        if (args is { Length: > 0 })
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("luatools://", StringComparison.OrdinalIgnoreCase))
                {
                    protocolUrl = arg;
                }
                else if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                {
                    startMinimized = true;
                }
                else if (arg.Equals("--tray-locked", StringComparison.OrdinalIgnoreCase))
                {
                    trayLocked = true;
                }
            }
        }

        // Only one instance may run: two copies share %AppData%\LuaToolsGui (auth.dat,
        // caches) and would lock each other's files (e.g. a token write failing on login).
        using var single = new Mutex(initiallyOwned: true, "LuaToolsGui.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // Launched from a browser protocol link → write the URL to a temp file so the live
            // instance can pick it up, then signal it to surface and handle the URL.
            if (protocolUrl is not null)
                Services.ProtocolService.WritePending(protocolUrl);

            // Already running (possibly hidden in the tray). Poke the live instance to surface its
            // window, then bow out. This is what users expect when they relaunch the app.
            // Exception: a --minimized relaunch explicitly wants to stay silent (this is what our
            // Steam DLL hijack passes on every launch: steam.exe loads it into more than one
            // process/thread per boot, so DllMain and this whole Main() can legitimately run more
            // than once in the same Steam startup). Don't surface the window for that duplicate.
            if (!startMinimized)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
                    {
                        signal.Set();
                        signal.Dispose();
                    }
                }
                catch { /* best effort. If signalling fails, just exit quietly */ }
            }

            // A --tray-locked relaunch (the loader passes this) asks the live instance to switch close-to-tray
            // on for the session. Otherwise, if the app was already open WITHOUT the flag when the loader
            // ran, it would stay killable by a window close (this second process exits without the live one
            // ever learning about the flag).
            if (trayLocked)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(EnableTrayLockEventName, out var tl))
                    {
                        tl.Set();
                        tl.Dispose();
                    }
                }
                catch { /* best effort */ }

                // A --tray-locked relaunch is the loader firing on Steam-open. Poke the live instance to
                // re-run its update flow (app + plugin), so an already-running app still updates when the
                // user opens Steam. Gated to --tray-locked so manual/protocol relaunches never trigger it.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(RecheckUpdatesEventName, out var ru))
                    {
                        ru.Set();
                        ru.Dispose();
                    }
                }
                catch { /* best effort */ }
            }
            return;
        }

        // First instance: store the URL so App.OnStartup can route it.
        StartupUrl = protocolUrl;
        StartMinimized = startMinimized;
        SessionTrayLock = trayLocked;

        // The live instance listens on this named event; a second launch sets it (above) to ask us to
        // restore/focus the main window. Created before App.Run so it exists before any relaunch races us.
        ShowWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        EnableTrayLockSignal = new EventWaitHandle(false, EventResetMode.AutoReset, EnableTrayLockEventName);
        RecheckUpdatesSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RecheckUpdatesEventName);

        var app = new App();
        app.InitializeComponent();
        app.Run();

        ShowWindowSignal.Dispose();
        EnableTrayLockSignal.Dispose();
        RecheckUpdatesSignal.Dispose();
    }

    /// <summary>Named event a second instance sets to ask the running one to surface its window.</summary>
    private const string ShowWindowEventName = "LuaToolsGui.ShowWindow";

    /// <summary>Owned by the live instance; App wires a listener that restores the window when it's set.</summary>
    internal static EventWaitHandle? ShowWindowSignal;

    /// <summary>Named event a second (--tray-locked) instance sets to ask the running one to enable
    /// close-to-tray for the session, so the loader's protection applies even to an already-open app.</summary>
    private const string EnableTrayLockEventName = "LuaToolsGui.EnableTrayLock";

    /// <summary>Owned by the live instance; App wires a listener that sets SessionTrayLock when it's set.</summary>
    internal static EventWaitHandle? EnableTrayLockSignal;

    /// <summary>Named event a --tray-locked second instance (the loader relaunching on Steam-open) sets to
    /// ask the running app to re-run its update flow. Otherwise an already-running app would never update
    /// just from the user opening Steam.</summary>
    private const string RecheckUpdatesEventName = "LuaToolsGui.RecheckUpdates";

    /// <summary>Owned by the live instance; App wires a listener that runs the update flow when it's set.</summary>
    internal static EventWaitHandle? RecheckUpdatesSignal;

    /// <summary>Protocol URL passed via command line on first launch; consumed by App.OnStartup.</summary>
    internal static string? StartupUrl;

    /// <summary>True when the app was launched with --minimized (from the loader DLL).</summary>
    internal static bool StartMinimized;

    /// <summary>True when launched with --tray-locked (the loader DLL passes this): the window's close
    /// button minimizes to the tray for THIS session regardless of the saved MinimizeToTray setting, so the
    /// backend can't be killed by an accidental window close. Only the tray "Exit" item fully quits. This
    /// is in-memory only and never written to settings, so a normal launch keeps the user's real setting.</summary>
    internal static bool SessionTrayLock;

    // Supported UI languages (a Strings.<tag>.resx exists for each). A saved/OS culture not in here
    // falls back to English. Keep in sync with the Settings language dropdown.
    private static readonly string[] SupportedLanguages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko",
        "es", "es-419", "pt-BR", "pt-PT", "fr", "de", "it", "nl", "pl",
        "ru", "uk", "tr", "ar",
        "cs", "hu", "ro", "el", "bg", "th", "vi", "id", "da", "fi", "nb", "sv",
    ];

    /// <summary>Resolve the UI language (saved setting → OS display language → English) and apply it to
    /// the thread + WPF before any UI is built.</summary>
    private static void ApplyUiCulture()
    {
        try
        {
            string tag = ResolveLanguageTag();
            var culture = new CultureInfo(tag);

            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;

            // Make WPF date/number formatting (and any xml:lang-aware rendering) follow the UI culture.
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
        catch
        {
            // Never let a culture problem stop the app from launching. Fall through to default (English).
        }
    }

    /// <summary>Saved language from settings.json if set + supported; else the OS display language if
    /// supported; else "en".</summary>
    private static string ResolveLanguageTag()
    {
        string? saved = ReadSavedLanguage();
        if (saved is not null && IsSupported(saved)) return saved;

        return MatchOsLanguage(CultureInfo.InstalledUICulture);
    }

    /// <summary>Map an OS culture to one of our supported tags. Tries the exact tag (e.g. "pt-BR"), then
    /// our regional variants, then the two-letter neutral parent (e.g. "es-ES" → "es", "fr-FR" → "fr").
    /// Falls back to "en". Internal-visible for tests.</summary>
    internal static string MatchOsLanguage(CultureInfo os)
    {
        // 1. Exact match: handles tags we ship verbatim (e.g. "pt-BR", "pt-PT", "zh-Hant", "es-419").
        if (IsSupported(os.Name)) return os.Name;

        string two = os.TwoLetterISOLanguageName; // "es", "fr", "pt", "zh", "nb"/"nn"...

        // 2. Chinese: script (Hans/Hant) matters more than region. zh-CN/zh-SG → Hans, zh-TW/HK/MO → Hant.
        if (two == "zh")
        {
            if (os.Name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0) return "zh-Hant";
            if (os.Name.IndexOf("Hans", StringComparison.OrdinalIgnoreCase) >= 0) return "zh-Hans";
            return os.Name is "zh-TW" or "zh-HK" or "zh-MO" ? "zh-Hant" : "zh-Hans";
        }

        // 3. Portuguese: we ship pt-BR + pt-PT but no neutral "pt". Default neutral/unknown → pt-PT (European).
        if (two == "pt") return "pt-PT";

        // 4. Spanish: we ship neutral "es" (Spain) + "es-419" (Latin America). Map LatAm regions to es-419.
        if (two == "es") return IsLatinAmericanSpanish(os.Name) ? "es-419" : "es";

        // 5. Norwegian: Bokmål ("nb") and Nynorsk ("nn") both fall back to our "nb".
        if (two is "nb" or "nn" or "no") return "nb";

        // 6. Generic: try the two-letter neutral parent (es-ES→es, fr-FR→fr, de-DE→de, ja-JP→ja, ...).
        if (IsSupported(two)) return two;

        return "en";
    }

    /// <summary>True for Spanish locales of Latin America (anything except Spain), so they map to es-419.</summary>
    private static bool IsLatinAmericanSpanish(string name) =>
        name.StartsWith("es-", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("es-ES", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupported(string tag) =>
        SupportedLanguages.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read just the "Language" key from %AppData%\LuaToolsGui\settings.json without spinning up
    /// the DI container (this runs before App is constructed).</summary>
    private static string? ReadSavedLanguage()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LuaToolsGui", "settings.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Language", out var el) && el.ValueKind == JsonValueKind.String)
            {
                string? v = el.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { /* corrupt/missing → fall back to OS */ }
        return null;
    }
}
