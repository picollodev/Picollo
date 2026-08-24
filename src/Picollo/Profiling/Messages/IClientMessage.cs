namespace Picollo.Profiling.Messages;

// ReSharper disable once EnumUnderlyingTypeIsInt
internal enum ClientMessageType : int
{
    SessionConfiguration = 1,
    InputChunk = 2,
    Start = 5,
    Stop = 6,
    Detach = 7,
    OnDetached = 8,
    OnAttached = 9,
    CallCounters = 10,
}

internal interface IClientMessage
{
    static abstract ClientMessageType MessageType { get; }
}

internal interface IClientMessage<T> : IClientMessage, IMessage<T> where T : IClientMessage<T>
{
}
