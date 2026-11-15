using UnityEngine;

/// <summary>
/// Đầu vào do AI điều khiển (thay HumanInput). Brain ghi Move và gọi Press...() ; các script hành động vẫn đọc qua CharacterInput.
/// Move là hướng trong THẾ GIỚI (x = Đông-Tây, y = Bắc-Nam) vì bot không có camera.
/// Press...() chỉ có hiệu lực đúng 1 khung hình, và là khung hình KẾ TIẾP, để script nào chạy trước hay sau BotController
/// trong cùng một khung hình cũng chỉ thấy phím "vừa bấm" đúng 1 lần.
/// </summary>
public class BotInput : MonoBehaviour, IInputProvider
{
    int crouchFrame = -1, interactFrame = -1, vaultFrame = -1, palletFrame = -1, attackFrame = -1, pickupFrame = -1, dropFrame = -1;

    public Vector2 Move { get; set; }
    public bool MoveIsWorldSpace => true;

    public bool CrouchDown => crouchFrame == Time.frameCount;
    public bool InteractDown => interactFrame == Time.frameCount;
    public bool VaultDown => vaultFrame == Time.frameCount;
    public bool PalletDown => palletFrame == Time.frameCount;
    public bool AttackDown => attackFrame == Time.frameCount;
    public bool PickupDown => pickupFrame == Time.frameCount;
    public bool DropDown => dropFrame == Time.frameCount;

    public void PressCrouch() => crouchFrame = Time.frameCount + 1;
    public void PressInteract() => interactFrame = Time.frameCount + 1;
    public void PressVault() => vaultFrame = Time.frameCount + 1;
    public void PressPallet() => palletFrame = Time.frameCount + 1;
    public void PressAttack() => attackFrame = Time.frameCount + 1;
    public void PressPickup() => pickupFrame = Time.frameCount + 1;
    public void PressDrop() => dropFrame = Time.frameCount + 1;
}
