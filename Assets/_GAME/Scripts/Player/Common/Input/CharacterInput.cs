using UnityEngine;

/// <summary>
/// Cổng đầu vào duy nhất của một nhân vật. Mọi script hành động (di chuyển, tương tác, đánh, bế, nhảy, pallet) đọc đầu vào ở đây.
/// Mặc định dùng HumanInput. Đổi nguồn bằng Use(...) / UseIdle() / UseHuman() (stand-in và bot sẽ dùng ở bước sau).
/// Không cần gắn tay vào prefab: script khác gọi CharacterInput.Of(gameObject) sẽ tự thêm nếu chưa có (kéo theo HumanInput).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(HumanInput))]
public class CharacterInput : MonoBehaviour
{
    HumanInput human;
    IInputProvider provider;

    /// <summary>Lấy CharacterInput của object này, chưa có thì tự thêm.</summary>
    public static CharacterInput Of(GameObject go)
    {
        CharacterInput c = go.GetComponent<CharacterInput>();
        if (c == null) c = go.AddComponent<CharacterInput>();
        return c;
    }

    void Awake() => EnsureInit();

    void EnsureInit()
    {
        if (human == null) human = GetComponent<HumanInput>();
        if (provider == null) provider = human;
    }

    /// <summary>Bảng phím của người chơi (để hiện gợi ý "[F] ..."). Luôn có.</summary>
    public HumanInput Keys { get { EnsureInit(); return human; } }

    public void Use(IInputProvider p) { EnsureInit(); provider = p ?? human; }
    public void UseHuman() { EnsureInit(); provider = human; }
    public void UseIdle() { EnsureInit(); provider = IdleInput.Instance; }

    public Vector2 Move { get { EnsureInit(); return provider.Move; } }
    public bool MoveIsWorldSpace { get { EnsureInit(); return provider.MoveIsWorldSpace; } }
    public bool CrouchDown { get { EnsureInit(); return provider.CrouchDown; } }
    public bool InteractDown { get { EnsureInit(); return provider.InteractDown; } }
    public bool VaultDown { get { EnsureInit(); return provider.VaultDown; } }
    public bool PalletDown { get { EnsureInit(); return provider.PalletDown; } }
    public bool AttackDown { get { EnsureInit(); return provider.AttackDown; } }
    public bool PickupDown { get { EnsureInit(); return provider.PickupDown; } }
    public bool DropDown { get { EnsureInit(); return provider.DropDown; } }
}
