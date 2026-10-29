using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Chơi qua INTERNET (WAN) bằng Unity Multiplayer Services (Sessions + Relay). Gắn vào GameObject NetworkManager.
/// - Tạo phòng: tạo session có Relay, trả về MÃ PHÒNG; SDK tự khởi động Host cho NetworkManager.
/// - Vào phòng: nhập mã phòng; SDK tự kết nối Client qua Relay.
/// - Mất kết nối giữa trận: tự thử nối lại (ReconnectToSessionAsync, nếu hỏng thì vào lại bằng mã).
/// - Bị server từ chối / host đóng phòng: hiện lý do và về Lobby, KHÔNG thử nối lại.
/// </summary>
public class RoomService : MonoBehaviour
{
    public static RoomService Instance { get; private set; }

    [Tooltip("Chỉ dùng khi test nhiều cửa sổ. Để trống: Editor tự dùng tên theo PID, bản build dùng tài khoản mặc định (PlayerId bền qua các lần mở game).")]
    [SerializeField] string profile = "";
    [SerializeField] string lobbySceneName = "Lobby";
    [SerializeField] int reconnectAttempts = 3;
    [SerializeField] float reconnectDelaySeconds = 3f;

    /// <summary>Phòng đang tham gia (null nếu chưa vào).</summary>
    public ISession Session { get; private set; }
    public string RoomCode => Session != null ? Session.Code : "";
    public bool InRoom => Session != null;
    public bool Busy { get; private set; }
    public bool IsReconnecting => reconnecting;

    /// <summary>Báo trạng thái ngắn cho UI (đang tạo phòng, lỗi, bị từ chối, đang nối lại...).</summary>
    public event Action<string> OnStatus;

    string lastRoomCode = "";
    string lastSessionId = "";
    bool intentionalLeave;   // true khi chính người dùng bấm Rời phòng
    bool reconnecting;
    bool inMatch;            // MatchManager báo: đang trong trận (chỉ khi đó mới tự nối lại)
    bool goingToLobby;
    bool reconnectDone;
    bool reconnectSuccess;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Start()
    {
        if (Instance != this) return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null) nm.OnClientDisconnectCallback += OnNetworkDisconnect;
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null) nm.OnClientDisconnectCallback -= OnNetworkDisconnect;
        Instance = null;
    }

    /// <summary>MatchManager gọi: true khi vào trận, false khi trận có kết quả.</summary>
    public void SetInMatch(bool on) { inMatch = on; }

    // ============================== Mất kết nối ==============================

    void OnNetworkDisconnect(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.IsServer) return;                      // chỉ xử lý phía client
        if (intentionalLeave) { intentionalLeave = false; return; } // người dùng tự rời
        if (reconnecting) return;

        // 1) Server chủ động từ chối hoặc đá ra (sai phiên bản, phòng đầy, host đóng phòng...): có lý do, không nối lại.
        string reason = nm.DisconnectReason;
        if (!string.IsNullOrEmpty(reason))
        {
            Status("Bị ngắt kết nối: " + reason);
            ForgetRoom();
            GoToLobby();
            return;
        }

        // 2) Rớt mạng ngoài ý muốn khi đang trong trận: tự nối lại.
        if (inMatch && !string.IsNullOrEmpty(lastRoomCode))
        {
            StartCoroutine(AutoReconnectRoutine());
            return;
        }

        // 3) Rớt mạng ở phòng chờ hoặc không có mã phòng (LAN): về menu, dọn phiên để có thể tạo/vào phòng lại.
        Status("Mất kết nối.");
        ForgetRoom();
        GoToLobby();
    }

    IEnumerator AutoReconnectRoutine()
    {
        reconnecting = true;
        Status("Mất kết nối! Đang thử nối lại...");

        // Chờ NetworkManager tắt hẳn (không dùng thời gian cố định).
        NetworkManager nm = NetworkManager.Singleton;
        float waited = 0f;
        while (nm != null && (nm.IsListening || nm.ShutdownInProgress))
        {
            waited += Time.unscaledDeltaTime;
            if (waited > 1f && nm.IsListening && !nm.ShutdownInProgress) nm.Shutdown();
            yield return null;
        }

        // Giữ lại tham chiếu cũ, KHÔNG rời phòng: còn là thành viên phòng thì mới Reconnect được.
        ISession stale = Session;
        Session = null;

        for (int attempt = 1; attempt <= reconnectAttempts; attempt++)
        {
            Status("Kết nối lại... (" + attempt + "/" + reconnectAttempts + ")");
            yield return new WaitForSecondsRealtime(reconnectDelaySeconds * attempt);   // chờ lâu dần

            reconnectDone = false;
            reconnectSuccess = false;
            ReconnectAttempt();
            yield return new WaitUntil(() => reconnectDone);

            if (reconnectSuccess)
            {
                Status("Đã nối lại, đang vào trận...");
                reconnecting = false;
                yield break;
            }
        }

        Status("Không nối lại được sau " + reconnectAttempts + " lần thử. Về phòng chờ.");
        reconnecting = false;
        ForgetRoom();
        LeaveQuietly(stale);
        GoToLobby();
    }

    // async void chạy trên luồng chính của Unity (SynchronizationContext).
    async void ReconnectAttempt()
    {
        bool ok = false;
        try { ok = await ReconnectOrJoinAsync(); }
        catch (Exception e) { Debug.LogWarning("[Room] Reconnect lỗi: " + e.Message); }
        reconnectSuccess = ok;
        reconnectDone = true;
    }

    async Task<bool> ReconnectOrJoinAsync()
    {
        if (!await InitAsync()) return false;
        LocalIdentity.ApplyConnectionData(NetworkManager.Singleton);

        // Cách 1 (chuẩn): còn là thành viên phòng thì Reconnect.
        if (!string.IsNullOrEmpty(lastSessionId))
        {
            Busy = true;
            try
            {
                Session = await MultiplayerService.Instance.ReconnectToSessionAsync(lastSessionId);
                lastRoomCode = Session.Code;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Room] ReconnectToSession thất bại, thử vào lại bằng mã: " + e.Message);
                Session = null;
            }
            finally { Busy = false; }
        }

        // Cách 2: đã bị gỡ khỏi phòng thì vào lại bằng mã.
        return await JoinRoomAsync(lastRoomCode);
    }

    // ============================== Dọn dẹp / về Lobby ==============================

    void ForgetRoom()
    {
        inMatch = false;
        lastRoomCode = "";
        lastSessionId = "";
        ISession s = Session;
        Session = null;
        LeaveQuietly(s);
    }

    async void LeaveQuietly(ISession s)
    {
        if (s == null) return;
        try { await s.LeaveAsync(); }
        catch (Exception e) { Debug.Log("[Room] (bỏ qua) " + e.Message); }
    }

    void GoToLobby()
    {
        if (goingToLobby) return;
        goingToLobby = true;
        StartCoroutine(GoToLobbyRoutine());
    }

    IEnumerator GoToLobbyRoutine()
    {
        // Chờ NetworkManager tắt hẳn rồi mới đổi scene.
        NetworkManager nm = NetworkManager.Singleton;
        float waited = 0f;
        while (nm != null && (nm.IsListening || nm.ShutdownInProgress))
        {
            waited += Time.unscaledDeltaTime;
            if (waited > 1f && nm.IsListening && !nm.ShutdownInProgress) nm.Shutdown();
            yield return null;
        }

        Cursor.lockState = CursorLockMode.None;   // trong trận con trỏ bị khóa
        Cursor.visible = true;
        if (SceneManager.GetActiveScene().name != lobbySceneName)
            SceneManager.LoadScene(lobbySceneName);
        goingToLobby = false;
    }

    void Status(string msg)
    {
        Debug.Log("[Room] " + msg);
        if (OnStatus != null) OnStatus(msg);
    }

    // ============================== Dịch vụ Unity ==============================

    async Task<bool> InitAsync()
    {
        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                string p = profile;
#if UNITY_EDITOR
                // Nhiều cửa sổ Editor cùng máy phải có tài khoản ẩn danh khác nhau. Bản build KHÔNG làm vậy,
                // để PlayerId giữ nguyên giữa các lần mở game (cần cho reconnect sau khi crash).
                if (string.IsNullOrWhiteSpace(p)) p = "p" + System.Diagnostics.Process.GetCurrentProcess().Id;
#endif
                InitializationOptions options = string.IsNullOrWhiteSpace(p)
                    ? new InitializationOptions()
                    : new InitializationOptions().SetProfile(p);
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
            Status("Đang tạo phòng...");
            if (!await InitAsync()) return false;
            LocalIdentity.ApplyConnectionData(NetworkManager.Singleton);

            SessionOptions options = new SessionOptions { MaxPlayers = LobbyManager.MaxPlayers }.WithRelayNetwork();
            Session = await MultiplayerService.Instance.CreateSessionAsync(options);
            lastRoomCode = Session.Code;
            lastSessionId = Session.Id;
            intentionalLeave = false;
            inMatch = false;
            Status("Đã tạo phòng. Mã phòng: " + Session.Code);
            return true;
        }
        catch (Exception e)
        {
            Session = null;
            Status("Không tạo được phòng: " + e.Message);
            return false;
        }
        finally { Busy = false; }
    }

    /// <summary>Client: vào phòng bằng mã. Có thể ở bất kỳ mạng nào.</summary>
    public async Task<bool> JoinRoomAsync(string code)
    {
        if (Busy || InRoom) return false;
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) { Status("Hãy nhập mã phòng."); return false; }

        Busy = true;
        try
        {
            Status("Đang vào phòng " + code + "...");
            if (!await InitAsync()) return false;
            LocalIdentity.ApplyConnectionData(NetworkManager.Singleton);

            Session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            lastRoomCode = Session.Code;
            lastSessionId = Session.Id;
            intentionalLeave = false;
            Status("Đã vào phòng " + Session.Code);
            return true;
        }
        catch (Exception e)
        {
            Session = null;
            Status("Không vào được phòng (sai mã, phòng đầy hoặc đã đóng): " + e.Message);
            return false;
        }
        finally { Busy = false; }
    }

    /// <summary>Rời phòng. Host rời thì báo lý do cho các client rồi xóa phòng; client rời thì chỉ rời. Sau đó tắt NetworkManager.</summary>
    public async Task LeaveAsync()
    {
        if (Busy) return;
        intentionalLeave = true;   // chủ động rời: không tự nối lại
        inMatch = false;
        lastRoomCode = "";
        lastSessionId = "";
        Busy = true;

        NetworkManager nm = NetworkManager.Singleton;
        try
        {
            // Host: đá từng client kèm lý do để họ về Lobby ngay thay vì thử nối lại.
            if (nm != null && nm.IsServer)
            {
                foreach (ulong id in new List<ulong>(nm.ConnectedClientsIds))
                    if (id != NetworkManager.ServerClientId) nm.DisconnectClient(id, "Host đã đóng phòng");
                await Task.Delay(300);   // cho gói tin kịp gửi trước khi tắt
            }
            if (Session != null)
            {
                if (Session.IsHost) await Session.AsHost().DeleteAsync();
                else await Session.LeaveAsync();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Room] Lỗi khi rời phòng: " + e.Message);
        }
        finally
        {
            Session = null;
            Busy = false;
        }

        if (nm != null && nm.IsListening) nm.Shutdown();
        Status("Đã rời phòng.");
    }
}