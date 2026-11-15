using UnityEngine;

/// <summary>
/// Nguồn đầu vào của một nhân vật. Người chơi dùng HumanInput (bàn phím/chuột), nhân vật đứng im dùng IdleInput,
/// sau này bot dùng BotInput. Các script hành động chỉ đọc đầu vào qua CharacterInput, không đọc UnityEngine.Input.
/// Các thuộc tính "...Down" là true đúng 1 khung hình lúc bấm (giống Input.GetKeyDown).
/// </summary>
public interface IInputProvider
{
    /// <summary>x = ngang, y = dọc, trong khoảng -1..1.</summary>
    Vector2 Move { get; }

    /// <summary>false: Move tính theo camera (người chơi). true: Move là hướng trong thế giới (x = Đông-Tây, y = Bắc-Nam), dùng cho bot không có camera.</summary>
    bool MoveIsWorldSpace { get; }

    bool CrouchDown { get; }
    bool InteractDown { get; }
    bool VaultDown { get; }
    bool PalletDown { get; }
    bool AttackDown { get; }
    bool PickupDown { get; }
    bool DropDown { get; }
}
