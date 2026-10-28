using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum MatchResult : byte { None = 0, SurvivorsWin = 1, HunterWins = 2, Draw = 3 }
public enum EndReason : byte { None = 0, EnoughEscaped = 1, EnoughEliminated = 2, AllResolved = 3, TimeUp = 4, HunterLeft = 5 }

/// <summary>
/// Đặt 1 GameObject trong scene Match, gắn NetworkObject + script này. Mọi quyết định chạy trên SERVER.
/// Tuần 2: đếm máy, còi, 2 cổng, hầm ngục, đếm người thoát.
/// Tuần 3: đếm người bị loại (hết giờ trên ghế tên lửa).
/// Tuần 4: LUẬT KẾT THÚC TRẬN và xử lý rớt kết nối:
///  - Survivor thắng khi có từ "Win Count" (3) người thoát.
///  - Hunter thắng khi có từ "Win Count" (3) người bị loại.
///  - Tất cả Survivor đều đã thoát hoặc bị loại: so sánh số thoát và số bị loại (2 thoát và 2 bị loại là HÒA).
///  - Hết giờ (nếu bật): so sánh số thoát và số bị loại, bằng nhau là hòa.
///  - Rớt mạng: Survivor rớt tính là bị loại; Hunter rớt thì Survivor thắng; host rớt thì cả phiên kết thúc (không reconnect).
/// Khi số Survivor ít hơn 4 (chế độ Debug), ngưỡng thắng tự hạ xuống bằng số Survivor.
/// </summary>
public class MatchManager : NetworkBehaviour
{
    public static MatchManager Instance { get; private set; }

    /// <summary>true khi đã có kết quả trận. Các script khác dùng để dừng hành động (đánh, tương tác...).</summary>
    public static bool Ended => Instance != null && Instance.Result.Value != (byte)MatchResult.None;

    public const byte DungeonHidden = 0, DungeonRevealed = 1, DungeonOpen = 2;

    [SerializeField] int machinesRequired = 5;
    [SerializeField] int machinesToRevealDungeon = 2;
    [Tooltip("Kéo 3 hầm vào đây. Để trống thì tự tìm tất cả Dungeon trong scene.")]
    [SerializeField] Dungeon[] dungeons;
    [SerializeField] string lobbyScene = "Lobby";
    [Tooltip("Tùy chọn: AudioSource có clip còi, phát trên mọi máy khi đủ máy.")]
    [SerializeField] AudioSource sirenAudio;

    [Header("Luật thắng/thua/hòa (tuần 4)")]
    [Tooltip("Số người thoát (Survivor thắng) hoặc bị loại (Hunter thắng) để kết thúc trận ngay.")]
    [SerializeField] int winCount = 3;
    [Tooltip("Thời lượng trận (giây). 0 = không giới hạn. Hết giờ thì so sánh số thoát và số bị loại, bằng nhau là hòa.")]
    [SerializeField] float matchDurationSeconds = 600f;
    [Tooltip("Chờ ngần này giây sau khi vào trận mới bắt đầu tính luật, để mọi người kịp spawn.")]
    [SerializeField] float startDelay = 3f;

    public readonly NetworkVariable<int> MachinesDone = new NetworkVariable<int>(0);
    public readonly NetworkVariable<int> Escaped = new NetworkVariable<int>(0);
    public readonly NetworkVariable<bool> GatesPowered = new NetworkVariable<bool>(false);
    public readonly NetworkVariable<byte> DungeonPhase = new NetworkVariable<byte>(DungeonHidden);
    /// <summary>Số Survivor còn trên bản đồ (chưa thoát, chưa bị loại, còn kết nối).</summary>
    public readonly NetworkVariable<int> AliveSurvivors = new NetworkVariable<int>(0);
    /// <summary>Số Survivor đã bị loại (hết giờ trên ghế, hoặc rớt mạng).</summary>
    public readonly NetworkVariable<int> Eliminated = new NetworkVariable<int>(0);

    /// <summary>Kết quả trận (MatchResult). None = đang chơi.</summary>
    public readonly NetworkVariable<byte> Result = new NetworkVariable<byte>((byte)MatchResult.None);
    /// <summary>Lý do kết thúc (EndReason).</summary>
    public readonly NetworkVariable<byte> Reason = new NetworkVariable<byte>((byte)EndReason.None);
    /// <summary>Số giây còn lại của trận, -1 nếu không giới hạn thời gian.</summary>
    public readonly NetworkVariable<int> TimeLeftSeconds = new NetworkVariable<int>(-1);
    /// <summary>True khi Hunter đang trong cửa sổ reconnect 30 giây. Mọi máy đọc để hiện UI "Đang chờ Hunter..." và để ghế tên lửa tạm dừng đếm ngược.</summary>
    public readonly NetworkVariable<bool> HunterReconnecting = new NetworkVariable<bool>(false);

    public int MachinesRequired => machinesRequired;
    public int TotalMachines { get; private set; }
    public bool IsEnded => Result.Value != (byte)MatchResult.None;

    Dungeon chosenDungeon;
    float nextCheck;

    // Server
    float rulesStartTime;
    int totalSurvivors;
    readonly HashSet<ulong> resolved = new HashSet<ulong>();   // Survivor đã được tính thoát/bị loại, để không tính hai lần

    void Awake() { Instance = this; }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        TotalMachines = FindObjectsByType<CipherMachine>(FindObjectsSortMode.None).Length;
        GatesPowered.OnValueChanged += OnGatesPoweredChanged;   // còi: chạy trên mọi máy

        if (IsServer)
        {
            PickDungeon();
            rulesStartTime = Time.time + startDelay;
            TimeLeftSeconds.Value = matchDurationSeconds > 0f ? Mathf.CeilToInt(matchDurationSeconds) : -1;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    public override void OnNetworkDespawn()
    {
        GatesPowered.OnValueChanged -= OnGatesPoweredChanged;
        if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        ReconnectManager.Instance?.Clear();   // xóa tất cả pending state khi match kết thúc / về lobby
        if (Instance == this) Instance = null;
    }

    void OnGatesPoweredChanged(bool oldValue, bool newValue)
    {
        if (newValue && sirenAudio != null) sirenAudio.Play();
    }

    void PickDungeon()
    {
        var pool = new List<Dungeon>();
        if (dungeons != null)
            foreach (Dungeon d in dungeons) if (d != null) pool.Add(d);
        if (pool.Count == 0) pool.AddRange(FindObjectsByType<Dungeon>(FindObjectsSortMode.None));

        if (pool.Count == 0)
        {
            Debug.LogWarning("MatchManager: scene không có Dungeon nào.");
            return;
        }
        chosenDungeon = pool[Random.Range(0, pool.Count)];
        Debug.Log("Hầm ngục được chọn: " + chosenDungeon.name);
    }

    // ---------------- Chỉ chạy trên server ----------------

    void Update()
    {
        if (!IsServer || !IsSpawned) return;
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.25f;

        int alive = CountAliveSurvivors();
        if (AliveSurvivors.Value != alive) AliveSurvivors.Value = alive;

        // Hầm đã lộ + chỉ còn đúng 1 Survivor sống => mở hầm.
        if (!IsEnded && chosenDungeon != null && DungeonPhase.Value == DungeonRevealed && alive == 1)
        {
            chosenDungeon.ServerOpen();
            DungeonPhase.Value = DungeonOpen;
        }

        UpdateMatchRules();
    }

    int CountAliveSurvivors()
    {
        int n = 0;
        foreach (var kv in NetworkManager.ConnectedClients)
        {
            NetworkObject po = kv.Value.PlayerObject;
            if (po == null) continue;
            PlayerRoleInfo info = po.GetComponent<PlayerRoleInfo>();
            if (info == null || info.CurrentRole != Role.Survivor) continue;
            SurvivorState st = po.GetComponent<SurvivorState>();
            if (st != null && st.IsOut) continue;
            n++;
        }
        return n;
    }

    // Số Survivor đang kết nối (kể cả người đã thoát hoặc bị loại nhưng còn trong phòng).
    int CountConnectedSurvivors()
    {
        int n = 0;
        foreach (var kv in NetworkManager.ConnectedClients)
        {
            NetworkObject po = kv.Value.PlayerObject;
            if (po == null) continue;
            PlayerRoleInfo info = po.GetComponent<PlayerRoleInfo>();
            if (info != null && info.CurrentRole == Role.Survivor) n++;
        }
        return n;
    }

    // ---------------- Luật kết thúc trận ----------------

    void UpdateMatchRules()
    {
        if (IsEnded || Time.time < rulesStartTime) return;

        // Tổng số Survivor của trận: lấy số lớn nhất từng thấy (người rớt mạng vẫn được tính vào tổng).
        totalSurvivors = Mathf.Max(totalSurvivors, CountConnectedSurvivors());
        if (totalSurvivors <= 0) return;     // chưa có Survivor nào, đang chờ spawn

        float timeLeft = float.MaxValue;
        if (matchDurationSeconds > 0f)
        {
            timeLeft = matchDurationSeconds - (Time.time - rulesStartTime);
            int secs = Mathf.Max(0, Mathf.CeilToInt(timeLeft));
            if (TimeLeftSeconds.Value != secs) TimeLeftSeconds.Value = secs;
        }

        int e = Escaped.Value;
        int x = Eliminated.Value;
        int needed = Mathf.Min(winCount, totalSurvivors);

        if (e >= needed) { End(MatchResult.SurvivorsWin, EndReason.EnoughEscaped); return; }
        if (x >= needed) { End(MatchResult.HunterWins, EndReason.EnoughEliminated); return; }

        if (e + x >= totalSurvivors)   // tất cả Survivor đều đã có kết quả (2 thoát và 2 bị loại là hòa)
        {
            End(Compare(e, x), EndReason.AllResolved);
            return;
        }

        if (matchDurationSeconds > 0f && timeLeft <= 0f)
            End(Compare(e, x), EndReason.TimeUp);
    }

    static MatchResult Compare(int escaped, int eliminated)
    {
        if (escaped > eliminated) return MatchResult.SurvivorsWin;
        if (eliminated > escaped) return MatchResult.HunterWins;
        return MatchResult.Draw;
    }

    void End(MatchResult result, EndReason reason)
    {
        if (!IsServer || IsEnded) return;
        Reason.Value = (byte)reason;
        Result.Value = (byte)result;
        Debug.Log($"[Match] KẾT THÚC: {result} ({reason}). Thoát {Escaped.Value}, bị loại {Eliminated.Value}.");
    }

    // ---------------- Rớt kết nối ----------------

    void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer || IsEnded) return;
        if (clientId == NetworkManager.ServerClientId) return;   // host rớt: cả phiên kết thúc

        // Lấy PlayerObject TRƯỚC khi NGO hủy nó (callback này luôn fire trước khi object bị destroy)
        NetworkObject playerObj = null;
        if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var c))
            playerObj = c.PlayerObject;

        Role role = RoleAssignments.Get(clientId);

        // Thử lưu state để cho phép reconnect trong 30 giây
        bool saved = playerObj != null
                  && ReconnectManager.Instance != null
                  && ReconnectManager.Instance.SaveAndHold(clientId, playerObj);

        if (role == Role.Hunter)
        {
            if (saved)
            {
                // Bật cờ "Hunter đang reconnect" → ghế tên lửa tạm dừng đếm ngược
                HunterReconnecting.Value = true;
                Debug.Log($"Hunter {clientId} rớt mạng, chờ reconnect {ReconnectManager.Window}s.");
            }
            else
            {
                // Không lưu được (playerId chưa đăng ký) → kết thúc ngay
                End(MatchResult.SurvivorsWin, EndReason.HunterLeft);
            }
            return;
        }

        // Survivor rớt mạng
        if (saved)
        {
            Debug.Log($"Survivor {clientId} rớt mạng, đang chờ reconnect {ReconnectManager.Window}s.");
        }
        else if (resolved.Add(clientId))
        {
            // Không lưu được state → tính là bị loại ngay
            Eliminated.Value++;
            Debug.Log($"Survivor {clientId} rớt mạng (không lưu được state), tính là bị loại. Tổng: {Eliminated.Value}");
        }
    }

    /// <summary>
    /// Gọi bởi ReconnectManager.Update() khi hết 30 giây mà player chưa reconnect.
    /// Lúc này mới áp dụng hậu quả thực sự.
    /// </summary>
    public void FinalizeDisconnect(ReconnectManager.SavedState state)
    {
        if (!IsServer || IsEnded) return;
        if (state.role == Role.Hunter)
        {
            HunterReconnecting.Value = false;
            End(MatchResult.SurvivorsWin, EndReason.HunterLeft);
        }
        else
        {
            if (resolved.Add(state.oldClientId))
            {
                Eliminated.Value++;
                Debug.Log($"Survivor {state.playerId} hết giờ reconnect, bị loại. Tổng: {Eliminated.Value}");
            }
        }
    }

    /// <summary>
    /// Gọi bởi PlayerIdentity.RegisterServerRpc khi reconnect thành công.
    /// Xóa trạng thái chờ (HunterReconnecting v.v.).
    /// </summary>
    public void OnPlayerReconnected(ulong newClientId, Role role)
    {
        if (!IsServer) return;
        if (role == Role.Hunter) HunterReconnecting.Value = false;
        Debug.Log($"[Reconnect] Client {newClientId} (vai {role}) reconnect thành công.");
    }

    // ---------------- Sự kiện trong trận ----------------

    public void OnMachineCompleted()
    {
        if (!IsServer || IsEnded) return;
        MachinesDone.Value++;

        if (MachinesDone.Value >= machinesToRevealDungeon && DungeonPhase.Value == DungeonHidden && chosenDungeon != null)
        {
            chosenDungeon.ServerReveal();
            DungeonPhase.Value = DungeonRevealed;
        }

        if (MachinesDone.Value >= machinesRequired && !GatesPowered.Value)
        {
            GatesPowered.Value = true;   // còi + thông báo trên mọi máy qua biến này
            foreach (ExitGate gate in FindObjectsByType<ExitGate>(FindObjectsSortMode.None))
                gate.SetPowered(true);
        }
    }

    /// <summary>Gắn cờ Đã thoát: ẩn nhân vật, khóa di chuyển, không bị tấn công. Gọi từ cổng và hầm.</summary>
    public void OnSurvivorEscaped(ulong clientId)
    {
        if (!IsServer || IsEnded) return;
        if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) || c.PlayerObject == null) return;

        PlayerRoleInfo info = c.PlayerObject.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return;   // Hunter không thoát

        SurvivorState st = c.PlayerObject.GetComponent<SurvivorState>();
        if (st == null) { Debug.LogError("Prefab Survivor thiếu SurvivorState."); return; }
        if (st.IsOut) return;                                            // đã tính rồi

        st.ServerSet(SurvivorHealth.Escaped);
        resolved.Add(clientId);
        Escaped.Value++;
        ReconnectManager.Instance?.Unregister(clientId);   // không cần theo dõi reconnect nữa
        Debug.Log($"Survivor {clientId} đã thoát. Tổng thoát: {Escaped.Value}");
    }

    /// <summary>Gắn cờ Bị loại: ẩn nhân vật, khóa di chuyển, tăng bộ đếm. Gọi từ ghế tên lửa khi hết giờ.</summary>
    public void OnSurvivorEliminated(ulong clientId)
    {
        if (!IsServer || IsEnded) return;
        if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) || c.PlayerObject == null) return;

        PlayerRoleInfo info = c.PlayerObject.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return;

        SurvivorState st = c.PlayerObject.GetComponent<SurvivorState>();
        if (st == null || st.IsOut) return;                              // đã tính rồi

        st.ServerSet(SurvivorHealth.Eliminated);
        resolved.Add(clientId);
        Eliminated.Value++;
        ReconnectManager.Instance?.Unregister(clientId);   // không cần theo dõi reconnect nữa
        Debug.Log($"Survivor {clientId} bị loại. Tổng bị loại: {Eliminated.Value}");
    }

    /// <summary>Nút "Về phòng chờ" (chỉ host): cả phòng quay lại scene Lobby để chơi trận mới.</summary>
    public void BackToLobby()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        nm.SceneManager.LoadScene(lobbyScene, LoadSceneMode.Single);
    }
}
