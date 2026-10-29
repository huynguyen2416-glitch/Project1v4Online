
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Màn hình bắt đầu (đặt trong scene Lobby, cùng scene với NetworkManager). Luồng:
///   MenuPanel: [Tạo phòng (Internet)] [Nhập mã phòng + Vào phòng] [Chơi LAN (nhập IP)]
///   Kết nối xong (Host hoặc Client) -> tự chuyển sang LobbyPanel (hiện MÃ PHÒNG, danh sách người, chọn bản đồ/nhân vật, Sẵn sàng/Bắt đầu).
///   Nút Rời phòng -> về MenuPanel.
/// Chơi LAN (cách cũ nhập IP) vẫn dùng được: bấm "Chơi LAN" để bật ConnectionUI.
/// </summary>
public class MainMenuController : MonoBehaviour
{
    [Header("Panel")]
    [SerializeField] GameObject menuPanel;
    [SerializeField] GameObject lobbyPanel;

    [Header("Menu")]
    [SerializeField] Button createButton;
    [SerializeField] Button joinButton;
    [SerializeField] Button lanButton;
    [SerializeField] InputField codeInput;
    [SerializeField] Text statusText;
    [Tooltip("ConnectionUI cũ (nhập IP). Nên TẮT sẵn; chỉ bật khi bấm Chơi LAN.")]
    [SerializeField] Behaviour lanConnectionUI;
    [SerializeField] InputField nameInput;
    [Header("Lobby")]
    [SerializeField] Text roomCodeText;
    [SerializeField] Button copyCodeButton;
    [SerializeField] Button leaveButton;

    bool lastConnected;
    bool inited;

    void Start()
    {
        if (codeInput != null)
        {
            codeInput.characterLimit = 8;
            codeInput.onValueChanged.AddListener(s =>
            {
                string u = s.ToUpperInvariant();
                if (u != s) codeInput.SetTextWithoutNotify(u);   // mã phòng luôn viết hoa
            });
        }
        if (createButton != null) createButton.onClick.AddListener(OnCreate);
        if (joinButton != null) joinButton.onClick.AddListener(OnJoin);
        if (lanButton != null) lanButton.onClick.AddListener(OnLan);
        if (copyCodeButton != null) copyCodeButton.onClick.AddListener(OnCopy);
        if (leaveButton != null) leaveButton.onClick.AddListener(OnLeave);
        if (nameInput != null) { nameInput.characterLimit = PlayerProfile.MaxLength; nameInput.text = PlayerProfile.Name; }

        if (lanConnectionUI != null) lanConnectionUI.enabled = false;
        if (RoomService.Instance != null) RoomService.Instance.OnStatus += SetStatus;

        ShowPanels(false);
        SetStatus("Tạo phòng mới hoặc nhập mã phòng của bạn bè.");
    }

    void OnDestroy()
    {
        if (RoomService.Instance != null) RoomService.Instance.OnStatus -= SetStatus;
    }

    void Update()
    {
        NetworkManager nm = NetworkManager.Singleton;
        bool connected = nm != null && nm.IsListening && (nm.IsServer || nm.IsConnectedClient);
        if (!inited || connected != lastConnected)
        {
            inited = true;
            lastConnected = connected;
            ShowPanels(connected);
            if (connected && lanConnectionUI != null) lanConnectionUI.enabled = false;
        }

        if (connected && roomCodeText != null)
        {
            string code = RoomService.Instance != null ? RoomService.Instance.RoomCode : "";
            roomCodeText.text = string.IsNullOrEmpty(code) ? "Phòng LAN (không có mã)" : "Mã phòng: " + code;
        }
    }

    void ShowPanels(bool connected)
    {
        if (menuPanel != null) menuPanel.SetActive(!connected);
        if (lobbyPanel != null) lobbyPanel.SetActive(connected);
    }

    void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }

    void SetBusy(bool busy)
    {
        if (createButton != null) createButton.interactable = !busy;
        if (joinButton != null) joinButton.interactable = !busy;
        if (lanButton != null) lanButton.interactable = !busy;
    }

    async void OnCreate()
    {
        if (nameInput != null) PlayerProfile.Name = nameInput.text;
        if (RoomService.Instance == null) { SetStatus("Thiếu RoomService trên object NetworkManager."); return; }
        SetBusy(true);
        await RoomService.Instance.CreateRoomAsync();   // thông báo kết quả qua OnStatus
        SetBusy(false);
    }

    async void OnJoin()
    {
        if (nameInput != null) PlayerProfile.Name = nameInput.text;
        if (RoomService.Instance == null) { SetStatus("Thiếu RoomService trên object NetworkManager."); return; }
        SetBusy(true);
        await RoomService.Instance.JoinRoomAsync(codeInput != null ? codeInput.text : "");
        SetBusy(false);
    }

    void OnLan()
    {
        if (nameInput != null) PlayerProfile.Name = nameInput.text;
        if (lanConnectionUI != null) lanConnectionUI.enabled = true;
        SetStatus("Chơi LAN: nhập IP của host ở khung bên trái rồi bấm Host hoặc Client.");
    }

    void OnCopy()
    {
        string code = RoomService.Instance != null ? RoomService.Instance.RoomCode : "";
        if (!string.IsNullOrEmpty(code)) GUIUtility.systemCopyBuffer = code;
    }

    async void OnLeave()
    {
        if (RoomService.Instance != null && RoomService.Instance.InRoom)
        {
            await RoomService.Instance.LeaveAsync();
        }
        else if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
        }
        SetStatus("Đã rời phòng.");
    }
}
