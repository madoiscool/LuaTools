using System;

namespace LuaToolsGui.Services.SAM.Native;

public class ClientInitializeException : Exception
{
    public ClientInitializeFailure Failure { get; }

    public ClientInitializeException(ClientInitializeFailure failure)
    {
        Failure = failure;
    }

    public ClientInitializeException(ClientInitializeFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public ClientInitializeException(ClientInitializeFailure failure, string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = failure;
    }
}
