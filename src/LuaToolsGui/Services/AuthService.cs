namespace LuaToolsGui.Services;

/// <summary>
/// Authentication-key store. Replaces the old Discord/Supabase login: the user pastes an
/// authentication key, it is persisted in settings (<c>settings.json</c>), and the app requires
/// it before gated actions (manifest/DLC/fix downloads). There is no token exchange, refresh or
/// session — the key itself is the credential, attached as the Bearer token by
/// <see cref="LuaToolsApiClient"/>.
/// </summary>
public class AuthService(SettingsService settings)
{
    // The pre-key login build persisted a DPAPI-encrypted session here. One-time cleanup on init.
    private static readonly string LegacyAuthFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "auth.dat");

    /// <summary>The stored authentication key, or null when none is set.</summary>
    public string? AuthKey => settings.AuthKey;

    /// <summary>True when no key is set. Gated actions check this (the name survives from the old
    /// guest model: no key = no access).</summary>
    public bool IsGuest => string.IsNullOrWhiteSpace(settings.AuthKey);

    /// <summary>True when a key is set.</summary>
    public bool IsSignedIn => !IsGuest;

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
