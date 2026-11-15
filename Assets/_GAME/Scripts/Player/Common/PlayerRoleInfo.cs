using Unity.Netcode;

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
