using LuaToolsGui.Services.SAM.Native.Types;

namespace LuaToolsGui.Services.SAM.Native.Callbacks;

public class UserStatsReceived : Callback<Types.UserStatsReceived>
{
    public UserStatsReceived()
    {
        Id = 1101;
        IsServer = false;
    }
}
