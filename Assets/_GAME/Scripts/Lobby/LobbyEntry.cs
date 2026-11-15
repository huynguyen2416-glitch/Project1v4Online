using System;
using Unity.Netcode;
using Unity.Collections;

public struct LobbyEntry : INetworkSerializable, IEquatable<LobbyEntry>
{
    public ulong ClientId;
    public bool Ready;
    public bool WantsHunter;
    public int CharacterIndex;
    public FixedString64Bytes Name;
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref Ready);
        serializer.SerializeValue(ref WantsHunter);
        serializer.SerializeValue(ref CharacterIndex);
        serializer.SerializeValue(ref Name);
    }

    public bool Equals(LobbyEntry o) =>
        ClientId == o.ClientId && Ready == o.Ready && WantsHunter == o.WantsHunter && CharacterIndex == o.CharacterIndex && Name == o.Name;
}
