using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Chọn BẢN ĐỒ và NHÂN VẬT trong phòng chờ. Đặt trên LobbyPanel, kéo LobbyManager và hai Dropdown vào.
///  - Dropdown bản đồ: chỉ HOST đổi được (người khác chỉ xem); đổi xong mọi người thấy ngay.
///  - Dropdown nhân vật: mỗi người tự chọn cho mình (dùng cho vai Survivor hoặc Hunter mà họ nhận khi bắt đầu).
/// Danh sách lấy từ LobbyManager.mapNames và characterNames nên thêm bản đồ/nhân vật chỉ cần sửa ở LobbyManager.
/// </summary>
public class LobbySelectionUI : MonoBehaviour
{
    [SerializeField] LobbyManager lobby;
    [SerializeField] Dropdown mapDropdown;
    [SerializeField] Dropdown characterDropdown;

    int builtMapCount = -1, builtCharCount = -1;
    float nextPoll;

    void Start()
    {
        if (mapDropdown != null) mapDropdown.onValueChanged.AddListener(i => { if (lobby != null) lobby.SetMap(i); });
        if (characterDropdown != null) characterDropdown.onValueChanged.AddListener(i => { if (lobby != null) lobby.SetCharacter(i); });
    }

    void OnEnable()
    {
        if (lobby != null) lobby.OnChanged += Refresh;
    }

    void OnDisable()
    {
        if (lobby != null) lobby.OnChanged -= Refresh;
    }

    void Update()
    {
        // Vòng dự phòng: LobbyManager có thể spawn sau khi UI bật.
        if (Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + 0.5f;
        Refresh();
    }

    void Refresh()
    {
        if (lobby == null || !lobby.IsSpawned) return;

        if (mapDropdown != null)
        {
            if (builtMapCount != lobby.MapCount)
            {
                var opts = new List<string>();
                for (int i = 0; i < lobby.MapCount; i++) opts.Add(lobby.MapName(i));
                mapDropdown.ClearOptions();
                mapDropdown.AddOptions(opts);
                builtMapCount = lobby.MapCount;
            }
            mapDropdown.SetValueWithoutNotify(Mathf.Clamp(lobby.MapIndex.Value, 0, Mathf.Max(0, lobby.MapCount - 1)));
            mapDropdown.interactable = lobby.IsServer;   // chỉ host chọn bản đồ
        }

        if (characterDropdown != null)
        {
            if (builtCharCount != lobby.CharacterCount)
            {
                var opts = new List<string>();
                for (int i = 0; i < lobby.CharacterCount; i++) opts.Add(lobby.CharacterName(i));
                characterDropdown.ClearOptions();
                characterDropdown.AddOptions(opts);
                builtCharCount = lobby.CharacterCount;
            }
            if (lobby.TryGetMine(out LobbyEntry mine))
                characterDropdown.SetValueWithoutNotify(Mathf.Clamp(mine.CharacterIndex, 0, Mathf.Max(0, lobby.CharacterCount - 1)));
        }
    }
}
