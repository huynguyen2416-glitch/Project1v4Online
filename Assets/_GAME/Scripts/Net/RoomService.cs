using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>
/// Chơi qua INTERNET (WAN) bằng Unity Multiplayer Services (Sessions + Relay). Gắn vào GameObject NetworkManager.
/// - Tạo phòng: server tạo 1 session có Relay, trả về MÃ PHÒNG. SDK tự khởi động Host của NetworkManager.
/// - Vào phòng: nhập mã phòng ở máy khác (khác mạng cũng được), SDK tự kết nối Client qua Relay.
/// Không cần mở port, không cần IP. Sau khi kết nối, phần còn lại (LobbyManager, chọn vai, vào trận) giữ nguyên như chơi LAN.
/// Yêu cầu: package Multiplayer Services (com.unity.services.multiplayer), project đã liên kết Unity Cloud project
/// và đã bật dịch vụ (Authentication ẩn danh, Relay) trong Unity Dashboard.
/// </summary>
public class RoomService : MonoBehaviour
{
    public static RoomService Instance { get; private set; }

    [SerializeField] int maxPlayers = 5;
    [Tooltip("Để trống: tự dùng tên theo process để nhiều cửa sổ cùng 1 máy không trùng tài khoản ẩn danh.")]
    [SerializeField] string profile = "";

    /// <summary>Phòng đang tham gia (null nếu chưa vào).</summary>
    public ISession Session { get; private set; }
    public string RoomCode => Session != null ? Session.Code : "";
    public bool InRoom => Session != null;
    public bool Busy { get; private set; }

    /// <summary>Báo trạng thái ngắn cho UI (đang tạo phòng, lỗi, đã vào phòng...).</summary>
    public event Action<string> OnStatus;

    // ── Reconnect ───────────────────────────────────────────────────────────
    string lastRoomCode  = "";
    bool intentionalLeave;   // set true khi user chủ động bấm Leave
    bool reconnecting;       // đang trong vòng retry
    bool reconnectSuccess;
    bool reconnectDone;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Start()
    {
        // Hook disconnect callback de phat hien mat ket noi ngoai y muon
        var nm = NetworkManager.Singleton;
        if (nm != null) nm.OnClientDisconnectCallback += OnNetworkDisconnect;
    }

    void OnDestroy()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null) nm.OnClientDisconnectCallback -= OnNetworkDisconnect;
    }

    // Phat hien mat ket noi (chi xu ly phia CLIENT)
    void OnNetworkDisconnect(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.IsServer) return;    // server side: bo qua
        if (intentionalLeave) { intentionalLeave = false; return; }  // user tu y roi phong
        if (reconnecting) return;                  // dang thu lai roi
        if (string.IsNullOrEmpty(lastRoomCode)) return;  // chua vao phong nao
        // Chi reconnect khi dang trong tran (MatchManager ton tai)
        if (MatchManager.Instance == null) return;

        Status("Mat ket noi! Dang thu ket noi lai...");
        StartCoroutine(AutoReconnectRoutine());
    }

    System.Collections.IEnumerator AutoReconnectRoutine()
    {
        reconnecting = true;
        string code = lastRoomCode;

        // Shut down NetworkManager cu (neu con)
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            nm.Shutdown();
            yield return new WaitForSeconds(0.5f);
        }
        Session = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Status($"Ket noi lai... ({attempt}/3)");
            yield return new WaitForSeconds(3f);

            reconnectDone = false;
            reconnectSuccess = false;
            DoReconnectAsync(code);
            yield return new WaitUntil(() => reconnectDone);

            if (reconnectSuccess)
            {
                Status("Da ket noi lai thanh cong!");
                reconnecting = false;
                yield break;
            }
        }

        // Tat ca 3 lan deu that bai
        Status("Khong the ket noi lai sau 3 lan thu. Ve man hinh chinh.");
        reconnecting = false;
        lastRoomCode = "";
    }

    // async void chay tren Unity main thread (SynchronizationContext), an toan voi Unity API
    async void DoReconnectAsync(string code)
    {
        reconnectSuccess = await JoinRoomAsync(code);
        reconnectDone = true;
    }

    void Status(string msg)
    {
        Debug.Log("[Room] " + msg);
        OnStatus?.Invoke(msg);
    }

    async Task<bool> InitAsync()
    {
        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                string p = string.IsNullOrWhiteSpace(profile) ? "p" + System.Diagnostics.Process.GetCurrentProcess().Id : profile;
                var options = new InitializationOptions().SetProfile(p);
                await UnityServices.InitializeAsync(options);
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            return true;
        }
        catch (Exception e)
        {
            Status("Không kết nối được dịch vụ Unity (kiểm tra mạng và cấu hình Unity Cloud project): " + e.Message);
            return false;
        }
    }

    /// <summary>Host: tạo phòng qua Relay. Thành công thì RoomCode có giá trị và NetworkManager đã chạy ở chế độ Host.</summary>
    public async Task<bool> CreateRoomAsync()
    {
        if (Busy || InRoom) return false;
        Busy = true;
        try
        {
            Status("Dang tao phong...");
            if (!await InitAsync()) return false;

            var options = new SessionOptions { MaxPlayers = maxPlayers }.WithRelayNetwork();
            Session = await MultiplayerService.Instance.CreateSessionAsync(options);
            lastRoomCode = Session.Code;   // luu code de reconnect
            Status("Da tao phong. Ma phong: " + Session.Code);
            return true;
        }
        catch (Exception e)
        {
            Session = null;
            Status("Khong tao duoc phong: " + e.Message);
            return false;
        }
        finally { Busy = false; }
    }

    /// <summary>Client: vào phòng bằng mã. Có thể ở bất kỳ mạng nào.</summary>
    public async Task<bool> JoinRoomAsync(string code)
    {
        if (Busy || InRoom) return false;
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) { Status("Hay nhap ma phong."); return false; }

        Busy = true;
        try
        {
            Status("Dang vao phong " + code + "...");
            if (!await InitAsync()) return false;

            Session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            lastRoomCode = Session.Code;   // luu code de reconnect
            Status("Da vao phong " + Session.Code);
            return true;
        }
        catch (Exception e)
        {
            Session = null;
            Status("Khong vao duoc phong (sai ma, phong day hoac da dong): " + e.Message);
            return false;
        }
        finally { Busy = false; }
    }

    /// <summary>Rời phòng. Host rời thì xóa phòng; client rời thì chỉ rời. Sau đó tắt NetworkManager.</summary>
    public async Task LeaveAsync()
    {
        if (Busy) return;
        intentionalLeave = true;   // bao hieu: day la disconnect co chu y, khong reconnect
        lastRoomCode = "";
        Busy = true;
        try
        {
            if (Session != null)
            {
                if (Session.IsHost) await Session.AsHost().DeleteAsync();
                else await Session.LeaveAsync();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Room] Loi khi roi phong: " + e.Message);
        }
        finally
        {
            Session = null;
            Busy = false;
        }

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) nm.Shutdown();
        Status("Da roi phong.");
    }
}
