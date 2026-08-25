using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LuaToolsGui.Services.SAM.Native;

public class Client : IDisposable
{
    public Wrappers.SteamClient018? SteamClient { get; private set; }
    public Wrappers.SteamUser012? SteamUser { get; private set; }
    public Wrappers.SteamUserStats013? SteamUserStats { get; private set; }
    public Wrappers.SteamUtils005? SteamUtils { get; private set; }
    public Wrappers.SteamApps001? SteamApps001 { get; private set; }
    public Wrappers.SteamApps008? SteamApps008 { get; private set; }

    private bool _isDisposed;
    private int _pipe;
    private int _user;

    private readonly List<ICallback> _callbacks = [];

    public void Initialize(long appId)
    {
        if (string.IsNullOrEmpty(Steam.GetInstallPath()))
        {
            throw new ClientInitializeException(ClientInitializeFailure.GetInstallPath, "failed to get Steam install path");
        }

        if (appId != 0)
        {
            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture));
        }

        if (!Steam.Load())
        {
            throw new ClientInitializeException(ClientInitializeFailure.Load, "failed to load SteamClient");
        }

        SteamClient = Steam.CreateInterface<Wrappers.SteamClient018>("SteamClient018");
        if (SteamClient == null)
        {
            throw new ClientInitializeException(ClientInitializeFailure.CreateSteamClient, "failed to create ISteamClient018");
        }

        _pipe = SteamClient.CreateSteamPipe();
        if (_pipe == 0)
        {
            throw new ClientInitializeException(ClientInitializeFailure.CreateSteamPipe, "failed to create pipe");
        }

        _user = SteamClient.ConnectToGlobalUser(_pipe);
        if (_user == 0)
        {
            throw new ClientInitializeException(ClientInitializeFailure.ConnectToGlobalUser, "failed to connect to global user");
        }

        SteamUtils = SteamClient.GetSteamUtils004(_pipe);
        if (appId > 0 && SteamUtils.GetAppId() != (uint)appId)
        {
            throw new ClientInitializeException(ClientInitializeFailure.AppIdMismatch, "appID mismatch");
        }

        SteamUser = SteamClient.GetSteamUser012(_user, _pipe);
        SteamUserStats = SteamClient.GetSteamUserStats013(_user, _pipe);
        SteamApps001 = SteamClient.GetSteamApps001(_user, _pipe);
        SteamApps008 = SteamClient.GetSteamApps008(_user, _pipe);
    }

    ~Client()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (SteamClient != null && _pipe > 0)
        {
            if (_user > 0)
            {
                SteamClient.ReleaseUser(_pipe, _user);
                _user = 0;
            }

            SteamClient.ReleaseSteamPipe(_pipe);
            _pipe = 0;
        }

        _isDisposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public TCallback CreateAndRegisterCallback<TCallback>()
        where TCallback : ICallback, new()
    {
        TCallback callback = new();
        _callbacks.Add(callback);
        return callback;
    }

    private bool _runningCallbacks;

    public void RunCallbacks(bool server)
    {
        if (_runningCallbacks) return;

        _runningCallbacks = true;

        while (Steam.GetCallback(_pipe, out var message, out _))
        {
            var callbackId = message.Id;
            foreach (var callback in _callbacks.Where(
                candidate => candidate.Id == callbackId &&
                             candidate.IsServer == server))
            {
                callback.Run(message.ParamPointer);
            }
            Steam.FreeLastCallback(_pipe);
        }

        _runningCallbacks = false;
    }
}
