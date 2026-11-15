using UnityEngine;

/// <summary>Đầu vào "không làm gì": nhân vật đứng im, không thao tác. Dùng cho nhân vật của người vừa rớt mạng (đứng im chờ).</summary>
public sealed class IdleInput : IInputProvider
{
    public static readonly IdleInput Instance = new IdleInput();
    IdleInput() { }

    public Vector2 Move => Vector2.zero;
    public bool MoveIsWorldSpace => false;
    public bool CrouchDown => false;
    public bool InteractDown => false;
    public bool VaultDown => false;
    public bool PalletDown => false;
    public bool AttackDown => false;
    public bool PickupDown => false;
    public bool DropDown => false;
}
