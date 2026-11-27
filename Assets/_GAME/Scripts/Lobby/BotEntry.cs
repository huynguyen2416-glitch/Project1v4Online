using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>Một bot trong phòng chờ. Nhân vật của bot được random lúc bắt đầu trận.</summary>
public struct BotEntry : INetworkSerializable, IEquatable<BotEntry>
{
    public int BotId;
    public bool IsHunter;
    public FixedString64Bytes Name;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref BotId);
        serializer.SerializeValue(ref IsHunter);
        serializer.SerializeValue(ref Name);
    }

    public bool Equals(BotEntry o) => BotId == o.BotId && IsHunter == o.IsHunter && Name == o.Name;
}
