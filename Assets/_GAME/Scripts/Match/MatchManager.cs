using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

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

    [Header("Kết thúc sớm khi không còn ai cứu")]
    [Tooltip("Bật: nếu mọi Survivor còn lại đều đang ngồi ghế tên lửa (không còn ai để cứu) thì coi như họ bị loại ngay, khỏi chờ hết giờ ghế.")]
    [SerializeField] bool endWhenNoRescuers = true;
    [Tooltip("Điều kiện trên phải đúng liên tục ngần này giây mới áp dụng (tránh nhầm trong lúc vừa cứu xong / đang đổi trạng thái).")]
    [SerializeField] float noRescuerDelay = 1.5f;

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
    readonly Dictionary<ulong, NetworkObject> playerObjects = new Dictionary<ulong, NetworkObject>();

    /// <summary>Đăng ký PlayerObject khi spawn để không phụ thuộc ConnectedClients khi disconnect.</summary>
    public void RegisterPlayerObject(ulong clientId, NetworkObject netObj)
    {
        playerObjects[clientId] = netObj;
    }

    /// <summary>Lấy PlayerObject của client, kể cả khi client đã disconnect.</summary>
    public NetworkObject GetPlayerObject(ulong clientId)
    {
        if (playerObjects.TryGetValue(clientId, out var obj) && obj != null)
            return obj;
        if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) && c.PlayerObject != null)
            return c.PlayerObject;

        // Fallback: tìm NetworkObject còn trong scene có OwnerClientId == clientId
        foreach (var no in FindObjectsByType<NetworkObject>(FindObjectsSortMode.None))
        {
            if (no != null && no.OwnerClientId == clientId && no.GetComponent<PlayerRoleInfo>() != null)
                return no;
        }
        return null;
    }

    void Awake() { Instance = this; }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        TotalMachines = FindObjectsByType<CipherMachine>(FindObjectsSortMode.None).Length;
        GatesPowered.OnValueChanged += OnGatesPoweredChanged;   // còi: chạy trên mọi máy
        Result.OnValueChanged += OnResultChanged;               // <-- THÊM
        if (RoomService.Instance != null) RoomService.Instance.SetInMatch(true);
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
        Result.OnValueChanged -= OnResultChanged;
        if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        ReconnectManager.Instance?.ClearMatchState();   // giữ idMap để trận sau vẫn reconnect được
        if (Instance == this) Instance = null;
    }

    void OnGatesPoweredChanged(bool oldValue, bool newValue)
    {
        if (newValue && sirenAudio != null) sirenAudio.Play();
    }
    void OnResultChanged(byte oldValue, byte newValue)
    {
        if (newValue != (byte)MatchResult.None && RoomService.Instance != null)
            RoomService.Instance.SetInMatch(false);   // trận đã có kết quả: không tự nối lại nữa
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
        // Đếm người chơi đang kết nối
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
        // Đếm thêm bot-controlled survivors (đã disconnect nhưng bot đang thay)
        foreach (var kv in BotController.ActiveBots)
        {
            BotController bot = kv.Value;
            if (bot == null || bot.RoleInfo == null || bot.RoleInfo.CurrentRole != Role.Survivor) continue;
            if (bot.Survivor != null && bot.Survivor.IsOut) continue;
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
        // Bot (do server điều khiển, không nằm trong ConnectedClients) cũng là Survivor của trận.
        foreach (var kv in BotController.ActiveBots)
        {
            BotController bot = kv.Value;
            if (bot != null && bot.RoleInfo != null && bot.RoleInfo.CurrentRole == Role.Survivor) n++;
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

        // ĐIỀU KIỆN THẮNG CHỈ DỰA VÀO KẾT QUẢ GAMEPLAY (DÙ BOT HAY NGƯỜI):
        // 1) Survivor thắng khi đủ người thoát qua cổng hoặc hầm
        if (e >= needed) { End(MatchResult.SurvivorsWin, EndReason.EnoughEscaped); return; }
        // 2) Hunter thắng khi đủ người bị loại trên ghế tên lửa
        if (x >= needed) { End(MatchResult.HunterWins, EndReason.EnoughEliminated); return; }

        // 3) Tất cả Survivor đều đã thoát hoặc bị loại trên ghế: so sánh kết quả
        if (e + x >= totalSurvivors)
        {
            End(Compare(e, x), EndReason.AllResolved);
            return;
        }

        // 4) Không còn ai cứu: mọi Survivor còn lại đều đang ngồi ghế -> họ chắc chắn bị loại, khỏi chờ hết giờ ghế.
        if (endWhenNoRescuers) CheckNoRescuers(e, x);

        // Bỏ điều kiện tự xử thua khi hết giờ: trận đấu tiếp tục cho tới khi phân định kết quả thực tế
    }

    float noRescuerSince = -1f;

    /// <summary>
    /// Số Survivor chưa thoát / chưa bị loại (= totalSurvivors - e - x) đúng bằng số người đang ngồi ghế
    /// => không còn Survivor nào hoạt động, gục, bị bế hay đang chờ nối lại để đi cứu. Giữ điều kiện này noRescuerDelay giây
    /// rồi loại luôn những người trên ghế; vòng kiểm tra luật kế tiếp sẽ tự quyết định thắng / thua / hòa như bình thường
    /// (ví dụ 2 thoát + 1 bị loại + 1 trên ghế => 2 thoát, 2 bị loại => hòa).
    /// </summary>
    void CheckNoRescuers(int escaped, int eliminated)
    {
        // Hunter đang trong cửa sổ reconnect: ghế tạm dừng đếm ngược nên cũng không kết thúc sớm.
        if (HunterReconnecting.Value) { noRescuerSince = -1f; return; }

        int unresolved = totalSurvivors - escaped - eliminated;
        var seated = new List<ulong>();
        foreach (RocketChair chair in RocketChair.All)
        {
            if (chair == null || !chair.IsSpawned || chair.IsFree) continue;
            ulong id = chair.OccupantId.Value;
            if (seated.Contains(id)) continue;
            NetworkObject po = GetPlayerObject(id);
            SurvivorState st = po != null ? po.GetComponent<SurvivorState>() : null;
            if (st != null && st.Current == SurvivorHealth.OnChair) seated.Add(id);
        }

        if (seated.Count == 0 || seated.Count != unresolved) { noRescuerSince = -1f; return; }

        if (noRescuerSince < 0f) { noRescuerSince = Time.time; return; }
        if (Time.time - noRescuerSince < noRescuerDelay) return;

        Debug.Log($"[Match] Không còn ai cứu: loại {seated.Count} Survivor đang trên ghế.");
        foreach (ulong id in seated) OnSurvivorEliminated(id);
        noRescuerSince = -1f;
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

        // Lấy PlayerObject của client rớt mạng
        NetworkObject playerObj = GetPlayerObject(clientId);

        Role role = RoleAssignments.Get(clientId);

        // Thử lưu state để cho phép reconnect trong 30 giây
        bool saved = ReconnectManager.Instance != null
                  && ReconnectManager.Instance.SaveAndHold(clientId, playerObj);

        // ── Chuyển ownership cho server & bật bot ──
        if (playerObj != null)
        {
            playerObj.ChangeOwnership(NetworkManager.ServerClientId);
            EnableOwnerComponents(playerObj.gameObject);

            BotController bot = BotController.Of(playerObj.gameObject);
            bot.BeginStandIn(clientId);
            Debug.Log($"[Match] Bot đã bật cho client {clientId}, object {playerObj.name}");
        }

        if (role == Role.Hunter)
        {
            Debug.Log($"Hunter {clientId} rớt mạng, bot Hunter tiếp quản và chờ reconnect.");
        }
        else
        {
            Debug.Log($"Survivor {clientId} rớt mạng, bot Survivor tiếp quản và chờ reconnect.");
        }
    }

    /// <summary>Bật lại các component kiểm tra IsOwner khi ownership chuyển sang server (bot).</summary>
    void EnableOwnerComponents(GameObject go)
    {
        // CharacterController cần bật để bot di chuyển được
        var cc = go.GetComponent<CharacterController>();
        var moveForCc = go.GetComponent<NetworkPlayerMovement>();
        // Không bật cưỡng bức: Survivor đang bị bế / ngồi ghế / đã rời trận phải để CharacterController tắt.
        if (cc != null) cc.enabled = moveForCc == null || moveForCc.ShouldControllerBeOn();

        var interactor = go.GetComponent<PlayerInteractor>();
        if (interactor != null) interactor.enabled = true;

        var pallet = go.GetComponent<PalletInteract>();
        if (pallet != null) pallet.enabled = true;

        var vault = go.GetComponent<PlayerVault>();
        if (vault != null) vault.enabled = true;

        var movement = go.GetComponent<NetworkPlayerMovement>();
        if (movement != null) movement.enabled = true;

        var attack = go.GetComponent<HunterAttack>();
        if (attack != null) attack.enabled = true;

        var carry = go.GetComponent<HunterCarry>();
        if (carry != null) carry.enabled = true;
    }

    /// <summary>
    /// Gọi bởi ReconnectManager.Update() khi hết thời gian chờ ban đầu.
    /// Bot VẪN TIẾP TỤC CHƠI thay người chơi. Kết quả trận đấu chỉ tính theo gameplay (thoát qua cổng hoặc chết trên ghế).
    /// </summary>
    public void FinalizeDisconnect(ReconnectManager.SavedState state)
    {
        if (!IsServer || IsEnded) return;

        Debug.Log($"[Match] Hết 30s reconnect ban đầu của {state.playerId} (vai {state.role}), bot vẫn tiếp tục thi đấu.");
    }

    /// <summary>
    /// Gọi bởi PlayerIdentity.RequestRestoreServerRpc khi reconnect thành công.
    /// Xóa trạng thái chờ (HunterReconnecting v.v.).
    /// </summary>
    public void OnPlayerReconnected(ulong newClientId, Role role, ulong oldClientId)
    {
        if (!IsServer) return;

        // Dọn dẹp bot nếu còn
        if (BotController.ActiveBots.TryGetValue(oldClientId, out BotController bot) && bot != null)
        {
            bot.Release();
            if (bot.NetObj != null && bot.NetObj.IsSpawned)
                bot.NetObj.Despawn();
        }

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
        NetworkObject po = GetPlayerObject(clientId);
        if (po == null) return;

        PlayerRoleInfo info = po.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return;   // Hunter không thoát

        SurvivorState st = po.GetComponent<SurvivorState>();
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
        NetworkObject po = GetPlayerObject(clientId);
        if (po == null) return;

        PlayerRoleInfo info = po.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return;

        SurvivorState st = po.GetComponent<SurvivorState>();
        if (st == null || st.IsOut) return;                              // đã tính rồi

        st.ServerSet(SurvivorHealth.Eliminated);
        resolved.Add(clientId);
        Eliminated.Value++;
        ReconnectManager.Instance?.Unregister(clientId);   // không cần theo dõi reconnect nữa
        Debug.Log($"Survivor {clientId} bị loại trên ghế. Tổng bị loại: {Eliminated.Value}");
    }

    /// <summary>Nút "Về phòng chờ" (chỉ host): cả phòng quay lại scene Lobby để chơi trận mới.</summary>
    public void BackToLobby()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        nm.SceneManager.LoadScene(lobbyScene, LoadSceneMode.Single);
    }
}