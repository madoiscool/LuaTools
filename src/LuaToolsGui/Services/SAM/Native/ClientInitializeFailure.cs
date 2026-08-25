namespace LuaToolsGui.Services.SAM.Native;

public enum ClientInitializeFailure
{
    Unknown = 0,
    GetInstallPath,
    Load,
    CreateSteamClient,
    CreateSteamPipe,
    ConnectToGlobalUser,
    AppIdMismatch,
}
