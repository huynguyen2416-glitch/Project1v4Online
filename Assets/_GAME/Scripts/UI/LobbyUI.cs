using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Giao diện phòng chờ. Gắn vào Canvas (KHÔNG gắn vào LobbyPanel, vì LobbyPanel bị ẩn/hiện bởi script này).
/// Chỉ đọc dữ liệu từ LobbyManager rồi gán vào chữ/nút; các nút gọi hàm public của LobbyManager qua OnClick.
/// </summary>
public class LobbyUI : MonoBehaviour
{
    [SerializeField] LobbyManager lobby;
    [SerializeField] GameObject lobbyPanel;       // ẩn khi chưa Host/Client
    [SerializeField] Text titleText;          // "PHÒNG CHỜ (n/5)"
    [SerializeField] Text playerListText;     // mỗi người một dòng
    [SerializeField] Text readyButtonLabel;   // chữ trên ReadyButton
    [SerializeField] Toggle hunterToggle;         // "Tôi muốn làm Hunter"
    [SerializeField] GameObject hostOnlyGroup;    // StartButton + DebugToggle, chỉ host thấy
    [SerializeField] Text statusText;         // thông báo lỗi khi bấm Bắt đầu

    bool subscribed;

    void Awake()
    {
        if (lobby == null) lobby = FindFirstObjectByType<LobbyManager>();
        if (statusText != null) statusText.text = "";
    }

    void OnDestroy() { Unsubscribe(); }

    void Update()
    {
        if (lobby == null) { subscribed = false; return; }

        bool ready = lobby.IsSpawned;
        if (lobbyPanel != null && lobbyPanel.activeSelf != ready) lobbyPanel.SetActive(ready);

        if (ready && !subscribed)
        {
            lobby.OnChanged += Refresh;
            lobby.OnMessage += ShowMessage;
            subscribed = true;
            Refresh();
        }
        else if (!ready && subscribed)
        {
            Unsubscribe();
     }

        if (ready && hostOnlyGroup != null && hostOnlyGroup.activeSelf != lobby.IsServer)
            hostOnlyGroup.SetActive(lobby.IsServer);
    }

    void Unsubscribe()
    {
        if (subscribed && lobby != null)
        {
            lobby.OnChanged -= Refresh;
            lobby.OnMessage -= ShowMessage;
        }
        subscribed = false;
    }

    void Refresh()
    {
        if (lobby == null || !lobby.IsSpawned) return;

        if (titleText != null) titleText.text = $"PHÒNG CHỜ ({lobby.Count}/{LobbyManager.MaxPlayers})";

        if (playerListText != null)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lobby.Count; i++)
            {
                LobbyEntry e = lobby.Get(i);
                string me = lobby.IsMe(e.ClientId) ? " (bạn)" : "";
                string ready = e.Ready ? "SẴN SÀNG" : "đang chờ";
                string hunter = e.WantsHunter ? "  [muốn Hunter]" : "";
                sb.AppendLine($"{e.Name.ToString()}{me}: {ready}{hunter}");
            }
            playerListText.text = sb.ToString();
        }

        if (lobby.TryGetMine(out LobbyEntry mine))
        {
            if (readyButtonLabel != null) readyButtonLabel.text = mine.Ready ? "Hủy sẵn sàng" : "Sẵn sàng";
            if (hunterToggle != null) hunterToggle.SetIsOnWithoutNotify(mine.WantsHunter);   // không kích OnClick
        }
        if (statusText != null) statusText.text = "";
    }

    void ShowMessage(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }
}