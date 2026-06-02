using System;

namespace Rpc.Generator;

[AttributeUsage(AttributeTargets.Interface)]
public sealed class RpcServiceAttribute : Attribute
{
    public string ServiceName { get; }
    public RpcServiceAttribute(string serviceName) => ServiceName = serviceName;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcMethodAttribute : Attribute
{
    public ushort PacketId { get; }
    public RpcMethodAttribute(ushort packetId) => PacketId = packetId;
}
