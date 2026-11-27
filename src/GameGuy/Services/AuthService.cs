namespace GameGuy.Services;

/// <summary>
/// Authentication-key store. Replaces the old Discord/Supabase login: the user pastes an
/// authentication key, it is persisted in settings (<c>settings.json</c>), and the app requires
/// it before gated actions (manifest/DLC/fix downloads). There is no token exchange, refresh or
/// session — the key itself is the credential, attached as the Bearer token by
/// <see cref="ApiClient"/>.
/// </summary>
public class AuthService(SettingsService settings)
{
    // The pre-key login build persisted a DPAPI-encrypted session here. One-time cleanup on init.
    private static readonly string LegacyAuthFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppConfig.DataDirName, "auth.dat");

    /// <summary>The stored authentication key, or null when none is set.</summary>
    public string? AuthKey => settings.AuthKey;

    /// <summary>True when no key is set. Gated actions check this (the name survives from the old
    /// guest model: no key = no access). Inverted so "no key" is a PRESENT bool — every gate reads
    /// <c>IsGuest</c>, and a missing binding would silently evaluate to false (= key present), which is
    /// the wrong way to fail on a login gate.</summary>
    public bool IsGuest => string.IsNullOrWhiteSpace(settings.AuthKey);

    /// <summary>True when a key is set (drives the "Key set" labels).</summary>
    public bool IsSignedIn => !IsGuest;

    /// <summary>The stored key, trimmed, or null when none is set. Exposed so the Settings page can
    /// mask it — showing nothing at all reads as "no key", which is exactly what a returning user is
    /// here to check and fix.</summary>
    public string? TrimmedKey
    {
        get
        {
            string? key = settings.AuthKey?.Trim();
            return string.IsNullOrEmpty(key) ? null : key;
        }
    }

    public event Action? AuthStateChanged;

    /// <summary>One-time cleanup of the legacy login session file. Returns true when a key is set.</summary>
    public Task<bool> InitializeAsync()
    {
        try { File.Delete(LegacyAuthFile); } catch { /* best effort */ }
        AuthStateChanged?.Invoke();
        return Task.FromResult(!IsGuest);
    }

    /// <summary>Save the key (trimmed). Empty input is ignored.</summary>
    public void SetKey(string key)
    {
        string trimmed = key.Trim();
        if (trimmed.Length == 0) return;
        settings.AuthKey = trimmed;
        AuthStateChanged?.Invoke();
    }

    /// <summary>Forget the stored key.</summary>
    public void ClearKey()
    {
        settings.AuthKey = null;
        AuthStateChanged?.Invoke();
    }

    /// <summary>Returns the stored key as the request credential. Throws when none is set.</summary>
    public Task<string> GetValidAccessTokenAsync()
    {
        string? key = settings.AuthKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new AuthException(Resources.Strings.Auth_Err_NoKey);
        return Task.FromResult(key.Trim());
    }
}

public class AuthException(string message) : Exception(message);
