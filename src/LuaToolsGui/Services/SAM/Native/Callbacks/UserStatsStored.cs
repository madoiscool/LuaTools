using LuaToolsGui.Services.SAM.Native.Types;

namespace LuaToolsGui.Services.SAM.Native.Callbacks;

public class UserStatsStored : Callback<Types.UserStatsStored>
{
    public UserStatsStored()
    {
        Id = 1102;
        IsServer = false;
    }
}
