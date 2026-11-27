using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Collections;
/// <summary>
/// Phòng chờ (scene Lobby). Đặt 1 GameObject trong scene Lobby, gắn NetworkObject + script này.
/// Tối đa 5 chỗ (người + bot). Host thêm / xóa bot (Survivor hoặc Hunter); bot random nhân vật khi bắt đầu trận. Server giữ danh sách, client gửi Ready / "Muốn làm Hunter" / Nhân vật qua ServerRpc.
/// Host chọn BẢN ĐỒ (MapIndex đồng bộ cho mọi người). Mỗi người chọn NHÂN VẬT (CharacterIndex) cho vai mình sẽ nhận.
/// Chạy giống nhau cho LAN và WAN (Relay): kết nối xong là NetworkManager đã chạy, script này không quan tâm cách kết nối.
/// Không vẽ giao diện: LobbyUI và LobbySelectionUI (Canvas) đọc dữ liệu qua các thuộc tính/sự kiện public bên dưới.
/// </summary>
public class LobbyManager : NetworkBehaviour
{
    public const int MaxPlayers = 5;

    [Header("Bản đồ (host chọn)")]
    [Tooltip("Tên scene của từng bản đồ (phải có trong Build Profiles). Phần tử đầu là mặc định.")]
    [SerializeField] string[] mapScenes = { "Match" };
    [Tooltip("Tên hiển thị tương ứng từng bản đồ.")]
    [SerializeField] string[] mapNames = { "Bản đồ 1" };

    [Header("Nhân vật (mỗi người chọn)")]
    [Tooltip("Tên hiển thị các nhân vật. Chỉ số này dùng cho cả Survivor lẫn Hunter: MatchSpawner lấy prefab thứ i của vai mình nhận.")]
    [SerializeField] string[] characterNames = { "Mặc định" };

    /// <summary>Bản đồ đang chọn (chỉ số trong mapScenes), đồng bộ cho mọi máy.</summary>
    public readonly NetworkVariable<int> MapIndex = new NetworkVariable<int>(0);

    NetworkList<LobbyEntry> entries;
    NetworkList<BotEntry> bots;
    bool debugAnyCount;
    int nextBotId;       // server
    int survivorBotSeq;  // server: đánh số tên "Bot Survivor n"

    /// <summary>Bắn khi danh sách hoặc bản đồ đổi, khi bật/tắt Debug hoặc khi vừa spawn (để UI vẽ lại).</summary>
    public event Action OnChanged;
    /// <summary>Thông báo cho người bấm Bắt đầu (chưa đủ người, còn người chưa sẵn sàng...).</summary>
    public event Action<string> OnMessage;

    public int Count => entries.Count;
    public LobbyEntry Get(int i) => entries[i];
    public bool DebugAnyCount => debugAnyCount;

    // ---------------- Bot ----------------
    public int BotCount => bots.Count;
    public BotEntry GetBot(int i) => bots[i];
    /// <summary>Số chỗ đã dùng: người + bot.</summary>
    public int TotalCount => entries.Count + bots.Count;
    public bool HunterBotPresent
    {
        get { for (int i = 0; i < bots.Count; i++) if (bots[i].IsHunter) return true; return false; }
    }
    public bool AnyHumanWantsHunter
    {
        get { for (int i = 0; i < entries.Count; i++) if (entries[i].WantsHunter) return true; return false; }
    }

    public int MapCount => mapScenes != null ? mapScenes.Length : 0;
    public string MapName(int i) => (mapNames != null && i >= 0 && i < mapNames.Length) ? mapNames[i] : (mapScenes != null && i >= 0 && i < mapScenes.Length ? mapScenes[i] : "?");
    public int CharacterCount => characterNames != null ? characterNames.Length : 0;
    public string CharacterName(int i) => (characterNames != null && i >= 0 && i < characterNames.Length) ? characterNames[i] : "?";

    void Awake()
    {
        entries = new NetworkList<LobbyEntry>();
        bots = new NetworkList<BotEntry>();
    }

    public override void OnNetworkSpawn()
    {
        entries.OnListChanged += HandleListChanged;
        bots.OnListChanged += HandleBotsChanged;
        MapIndex.OnValueChanged += HandleMapChanged;

        if (IsServer)
        {
            foreach (ulong id in NetworkManager.ConnectedClientsIds) AddClient(id);
            NetworkManager.OnClientConnectedCallback += AddClient;
            NetworkManager.OnClientDisconnectCallback += RemoveClient;
        }
        OnChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        entries.OnListChanged -= HandleListChanged;
        bots.OnListChanged -= HandleBotsChanged;
        MapIndex.OnValueChanged -= HandleMapChanged;
        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= AddClient;
            NetworkManager.OnClientDisconnectCallback -= RemoveClient;
        }
    }

    void HandleListChanged(NetworkListEvent<LobbyEntry> e) => OnChanged?.Invoke();
    void HandleBotsChanged(NetworkListEvent<BotEntry> e) => OnChanged?.Invoke();
    void HandleMapChanged(int oldValue, int newValue) => OnChanged?.Invoke();

    int IndexOf(ulong id)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].ClientId == id) return i;
        return -1;
    }

    /// <summary>Lấy dòng của chính máy này trong danh sách.</summary>
    public bool TryGetMine(out LobbyEntry mine)
    {
        mine = default;
        if (!IsSpawned) return false;
        int i = IndexOf(NetworkManager.LocalClientId);
        if (i < 0) return false;
        mine = entries[i];
        return true;
    }

    public bool IsMe(ulong clientId) => IsSpawned && clientId == NetworkManager.LocalClientId;

    // ---------------- Server: quản lý danh sách ----------------

    void AddClient(ulong id)
    {
        if (IndexOf(id) >= 0) return;
        // Phòng đầy mà còn bot: nhường chỗ cho người thật bằng cách bỏ bot cuối.
        while (entries.Count + bots.Count >= MaxPlayers && bots.Count > 0) bots.RemoveAt(bots.Count - 1);
        if (entries.Count >= MaxPlayers)
        {
            Debug.LogWarning("Phòng đã đủ 5 người, từ chối client " + id);
            NetworkManager.DisconnectClient(id);
            return;
        }
        entries.Add(new LobbyEntry { ClientId = id, Name = new FixedString64Bytes(NameAssignments.Get(id)) });
    }

    void RemoveClient(ulong id)
    {
        int i = IndexOf(id);
        if (i >= 0) entries.RemoveAt(i);
    }

    [ServerRpc(RequireOwnership = false)]
    void SetReadyServerRpc(bool ready, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        LobbyEntry e = entries[i];
        e.Ready = ready;
        entries[i] = e;
    }

    [ServerRpc(RequireOwnership = false)]
    void WantHunterServerRpc(bool want, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        if (want && HunterBotPresent) return;   // đã có Hunter bot
        LobbyEntry e = entries[i];
        e.WantsHunter = want;
        entries[i] = e;
    }

    [ServerRpc(RequireOwnership = false)]
    void SetCharacterServerRpc(int characterIndex, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        if (characterIndex < 0 || characterIndex >= Mathf.Max(1, CharacterCount)) return;   // server kiểm tra chỉ số hợp lệ
        LobbyEntry e = entries[i];
        e.CharacterIndex = characterIndex;
        entries[i] = e;
    }

    // ---------------- Hàm public cho nút UI (OnClick) ----------------

    /// <summary>Nút Sẵn sàng / Hủy sẵn sàng.</summary>
    public void ToggleReady()
    {
        if (TryGetMine(out LobbyEntry mine)) SetReadyServerRpc(!mine.Ready);
    }

    /// <summary>Toggle "Tôi muốn làm Hunter" (OnClick, chọn Dynamic bool).</summary>
    public void SetWantHunter(bool want)
    {
        if (TryGetMine(out LobbyEntry mine) && mine.WantsHunter != want) WantHunterServerRpc(want);
    }

    /// <summary>Chọn nhân vật (chỉ số trong characterNames).</summary>
    public void SetCharacter(int characterIndex)
    {
        if (TryGetMine(out LobbyEntry mine) && mine.CharacterIndex != characterIndex) SetCharacterServerRpc(characterIndex);
    }

    /// <summary>Host chọn bản đồ. Người khác gọi hàm này không có tác dụng.</summary>
    public void SetMap(int mapIndex)
    {
        if (!IsSpawned || !IsServer) return;
        if (mapIndex < 0 || mapIndex >= MapCount) return;
        MapIndex.Value = mapIndex;
    }

    /// <summary>Toggle Debug: bắt đầu trận với số người tùy ý (chỉ có tác dụng trên host).</summary>
    public void SetDebug(bool on)
    {
        debugAnyCount = on;
        OnChanged?.Invoke();
    }

    // ---------------- Bot (chỉ host) ----------------

    /// <summary>Nút "+ Thêm bot": thêm một bot Survivor.</summary>
    public void AddBot()
    {
        if (!IsSpawned || !IsServer) return;
        if (TotalCount >= MaxPlayers) { Say("Phòng đã đủ " + MaxPlayers + " chỗ."); return; }
        AppendBot(false);
    }

    /// <summary>
    /// Nút "Lấp đầy": thêm bot cho đến khi đủ chỗ. Nếu chưa ai làm Hunter (không người nào tick, chưa có Hunter bot)
    /// thì bot đầu tiên được thêm là Hunter, còn lại là Survivor.
    /// </summary>
    public void FillWithBots()
    {
        if (!IsSpawned || !IsServer) return;
        if (TotalCount >= MaxPlayers) { Say("Phòng đã đủ " + MaxPlayers + " chỗ."); return; }
        bool needHunter = !HunterBotPresent && !AnyHumanWantsHunter;
        while (TotalCount < MaxPlayers)
        {
            AppendBot(needHunter);
            needHunter = false;
        }
    }

    /// <summary>Đổi vai của một bot giữa Survivor và Hunter (chỉ được 1 Hunter trong phòng).</summary>
    public void ToggleBotRole(int botId)
    {
        if (!IsSpawned || !IsServer) return;
        for (int i = 0; i < bots.Count; i++)
        {
            if (bots[i].BotId != botId) continue;
            BotEntry b = bots[i];
            if (!b.IsHunter)
            {
                if (HunterBotPresent) { Say("Chỉ có 1 Hunter: đã có Hunter bot."); return; }
                if (AnyHumanWantsHunter) { Say("Đã có người chọn làm Hunter. Bỏ chọn Hunter trước khi đổi bot thành Hunter."); return; }
                b.IsHunter = true;
                b.Name = new FixedString64Bytes("Bot Hunter");
            }
            else
            {
                b.IsHunter = false;
                b.Name = new FixedString64Bytes("Bot Survivor " + (++survivorBotSeq));
            }
            bots[i] = b;
            return;
        }
    }

    /// <summary>Xóa một bot khỏi phòng.</summary>
    public void RemoveBot(int botId)
    {
        if (!IsSpawned || !IsServer) return;
        for (int i = 0; i < bots.Count; i++)
            if (bots[i].BotId == botId) { bots.RemoveAt(i); return; }
    }

    void AppendBot(bool hunter)
    {
        string name = hunter ? "Bot Hunter" : "Bot Survivor " + (++survivorBotSeq);
        bots.Add(new BotEntry { BotId = nextBotId++, IsHunter = hunter, Name = new FixedString64Bytes(name) });
    }

    /// <summary>Nút BẮT ĐẦU TRẬN (chỉ host).</summary>
    public void StartMatch()
    {
        if (!IsSpawned || !IsServer) return;

        int humans = entries.Count;
        int total = humans + bots.Count;
        if (humans == 0) return;

        if (!debugAnyCount && total < MaxPlayers)
        {
            Say($"Cần đủ {MaxPlayers} người ({total}/{MaxPlayers}). Thêm bot hoặc bật debug.");
            return;
        }
        for (int i = 0; i < humans; i++)
        {
            // Host bấm Start nên không bắt buộc host phải bấm Sẵn sàng.
            if (!entries[i].Ready && entries[i].ClientId != NetworkManager.LocalClientId)
            {
                Say("Còn người chưa sẵn sàng: " + entries[i].Name.ToString());
                return;
            }
        }

        // Chọn Hunter: Hunter bot nếu có; không thì người tick "Muốn làm Hunter"; không ai tick thì random trong số người.
        // Chỉ 1 chỗ trong phòng (debug): mặc định là Survivor để test máy/cổng/hầm; tick Hunter thì làm Hunter.
        ulong hunterId = ulong.MaxValue;
        for (int i = 0; i < bots.Count; i++)
            if (bots[i].IsHunter) hunterId = BotRoster.IdOf(bots[i].BotId);

        if (hunterId == ulong.MaxValue)
        {
            if (total >= 2)
            {
                var wants = new System.Collections.Generic.List<ulong>();
                var all = new System.Collections.Generic.List<ulong>();
                for (int i = 0; i < humans; i++)
                {
                    all.Add(entries[i].ClientId);
                    if (entries[i].WantsHunter) wants.Add(entries[i].ClientId);
                }
                var pool = wants.Count > 0 ? wants : all;
                hunterId = pool[UnityEngine.Random.Range(0, pool.Count)];
            }
            else if (entries[0].WantsHunter)
            {
                hunterId = entries[0].ClientId;
            }
        }

        RoleAssignments.Clear();
        CharacterAssignments.Clear();
        SlotAssignments.Clear();
        BotRoster.Clear();

        int slot = 0;
        for (int i = 0; i < humans; i++)
        {
            ulong id = entries[i].ClientId;
            RoleAssignments.Set(id, id == hunterId ? Role.Hunter : Role.Survivor);
            CharacterAssignments.Set(id, entries[i].CharacterIndex);
            SlotAssignments.Set(id, slot++);
        }
        for (int i = 0; i < bots.Count; i++)
        {
            ulong id = BotRoster.IdOf(bots[i].BotId);
            RoleAssignments.Set(id, bots[i].IsHunter ? Role.Hunter : Role.Survivor);
            CharacterAssignments.Set(id, UnityEngine.Random.Range(0, Mathf.Max(1, CharacterCount)));   // bot: nhân vật ngẫu nhiên
            NameAssignments.Set(id, bots[i].Name.ToString());
            SlotAssignments.Set(id, slot++);
            BotRoster.Add(id);
        }

        string scene = mapScenes[Mathf.Clamp(MapIndex.Value, 0, mapScenes.Length - 1)];
        Debug.Log($"[Lobby] Bắt đầu trận: {humans} người + {bots.Count} bot, bản đồ \"{scene}\", Hunter = " +
                  (hunterId == ulong.MaxValue ? "không có" : "#" + hunterId));

        NetworkManager.SceneManager.LoadScene(scene, LoadSceneMode.Single);
    }

    void Say(string msg)
    {
        Debug.LogWarning(msg);
        OnMessage?.Invoke(msg);
    }
}
