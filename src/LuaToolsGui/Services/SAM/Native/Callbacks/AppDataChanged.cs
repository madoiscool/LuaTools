using LuaToolsGui.Services.SAM.Native.Types;

namespace LuaToolsGui.Services.SAM.Native.Callbacks;

public class AppDataChanged : Callback<Types.AppDataChanged>
{
    public AppDataChanged()
    {
        Id = 1005;
        IsServer = false;
    }
}
