using UnityEngine;

/// <summary>
/// Đọc bàn phím/chuột của người chơi. Đồng thời là BẢNG PHÍM của nhân vật (các script hiện gợi ý phím đọc từ đây).
/// Giá trị mặc định giống hệt các script cũ (C ngồi, F tương tác/bế, Q thả, Space nhảy/pallet, chuột trái đánh).
/// </summary>
public class HumanInput : MonoBehaviour, IInputProvider
{
    [Header("Phím")]
    [SerializeField] KeyCode crouchKey = KeyCode.C;
    [SerializeField] KeyCode interactKey = KeyCode.F;
    [SerializeField] KeyCode vaultKey = KeyCode.Space;
    [SerializeField] KeyCode palletKey = KeyCode.Space;
    [SerializeField] KeyCode pickupKey = KeyCode.F;
    [SerializeField] KeyCode dropKey = KeyCode.Q;
    [SerializeField] int attackMouseButton = 0;

    public KeyCode CrouchKey => crouchKey;
    public KeyCode InteractKey => interactKey;
    public KeyCode VaultKey => vaultKey;
    public KeyCode PalletKey => palletKey;
    public KeyCode PickupKey => pickupKey;
    public KeyCode DropKey => dropKey;

    public Vector2 Move => new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    public bool MoveIsWorldSpace => false;

    public bool CrouchDown => Input.GetKeyDown(crouchKey);
    public bool InteractDown => Input.GetKeyDown(interactKey);
    public bool VaultDown => Input.GetKeyDown(vaultKey);
    public bool PalletDown => Input.GetKeyDown(palletKey);
    public bool AttackDown => Input.GetMouseButtonDown(attackMouseButton);
    public bool PickupDown => Input.GetKeyDown(pickupKey);
    public bool DropDown => Input.GetKeyDown(dropKey);
}
