using System.Collections.Generic;
using Unity.Netcode;

public enum Role : byte { Survivor = 0, Hunter = 1 }

/// <summary>Server ghi nhớ ai làm vai gì (chỉ dùng trên server/host, sống qua việc đổi scene).</summary>
public static class RoleAssignments
{
    static readonly Dictionary<ulong, Role> map = new Dictionary<ulong, Role>();
    public static void Clear() => map.Clear();
    public static void Set(ulong clientId, Role role) => map[clientId] = role;
    public static Role Get(ulong clientId) => map.TryGetValue(clientId, out Role r) ? r : Role.Survivor;
    public static bool Has(ulong clientId) => map.ContainsKey(clientId);     
}

/// <summary>Gắn vào prefab Survivor và Hunter. Mọi máy đọc được vai của từng người chơi.</summary>
public class PlayerRoleInfo : NetworkBehaviour
{
    public readonly NetworkVariable<byte> RoleValue = new NetworkVariable<byte>((byte)Role.Survivor);

    public Role CurrentRole => (Role)RoleValue.Value;

    public override void OnNetworkSpawn()
    {
        if (IsServer) RoleValue.Value = (byte)RoleAssignments.Get(OwnerClientId);
    }
}