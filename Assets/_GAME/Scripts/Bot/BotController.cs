using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// "Bộ não ngoài" của một nhân vật khi nó không do người điều khiển. FSM vòng ngoài: Off -> Frozen -> Bot.
///   Off    : người chơi tự điều khiển (HumanInput).
///   Frozen : đứng im (IdleInput) frozenSeconds giây, chờ người rớt mạng quay lại.
///   Bot    : BotBrain (cây quyết định) chạy khoảng 4 lần/giây; BotNavigator dẫn đường mỗi khung hình.
/// Khi người chơi rớt mạng, SERVER chuyển ownership về cho mình rồi bật bot (BeginStandIn).
/// Bot chạy trên SERVER vì server bây giờ là owner (di chuyển là owner-authoritative).
/// Không cần gắn vào prefab: BotController.Of(gameObject) tự thêm khi cần.
/// </summary>
[DisallowMultipleComponent]
public class BotController : MonoBehaviour
{
    public enum BotState { Off, Frozen, Bot }

    [Header("Rớt mạng")]
    [Tooltip("Đứng im bao lâu thì bot tiếp quản.")]
    [SerializeField] float frozenSeconds = 20f;

    [Header("AI")]
    [Tooltip("Khoảng cách giữa hai lần suy nghĩ (giây). 0.25 = 4 lần/giây.")]
    [SerializeField] float thinkInterval = 0.25f;
    [Tooltip("Survivor bot có đi cứu đồng đội trên ghế và hồi máu khi Hunter ở xa không.")]
    [SerializeField] bool helpTeammates = true;

    [Header("Debug")]
    [SerializeField] bool showDebugLabel = true;

    public BotState State { get; private set; } = BotState.Off;
    public float ThinkInterval => thinkInterval;
    public bool HelpTeammates => helpTeammates;
    public string Who => $"{name}#{OriginalClientId}";

    /// <summary>ClientId gốc của người chơi đã disconnect (dùng cho RPC, CanUse, map lại khi reconnect).</summary>
    public ulong OriginalClientId { get; private set; }

    /// <summary>OwnerClientId thực tế (sau ChangeOwnership sẽ là ServerClientId).</summary>
    public ulong OwnerClientId => netObj != null ? netObj.OwnerClientId : 0UL;
    public string Goal => brain != null ? brain.Goal : "";

    /// <summary>Tất cả bot đang hoạt động, key = OriginalClientId.</summary>
    public static readonly Dictionary<ulong, BotController> ActiveBots = new Dictionary<ulong, BotController>();

    public BotInput Pad { get; private set; }
    public BotNavigator Nav { get; private set; }
    public NetworkPlayerMovement Move { get; private set; }
    public PlayerRoleInfo RoleInfo { get; private set; }
    public SurvivorState Survivor { get; private set; }
    public PlayerInteractor Interactor { get; private set; }
    public PalletInteract Pallet { get; private set; }
    public PlayerVault Vault { get; private set; }
    public HunterCarry Carry { get; private set; }
    public HunterAttack Attack { get; private set; }
    public NetworkObject NetObj => netObj;

    NetworkObject netObj;
    CharacterInput character;
    BotBrain brain;
    float frozenLeft;
    float nextThink;
    float lastThinkError = -99f;

    /// <summary>
    /// ClientId "thật" của nhân vật: id gốc nếu bot đang thay người chơi, ngược lại là OwnerClientId.
    /// Dùng thay cho OwnerClientId ở mọi chỗ cần biết "nhân vật này là của ai" (khi bot tiếp quản, owner là host).
    /// </summary>
    public static ulong EffectiveClientId(NetworkBehaviour nb)
    {
        BotController bc = nb.GetComponent<BotController>();
        return bc != null && bc.State != BotState.Off ? bc.OriginalClientId : nb.OwnerClientId;
    }

    public static BotController Of(GameObject go)
    {
        BotController c = go.GetComponent<BotController>();
        if (c == null) c = go.AddComponent<BotController>();
        return c;
    }

    void Awake()
    {
        netObj = GetComponent<NetworkObject>();
        Move = GetComponent<NetworkPlayerMovement>();
        RoleInfo = GetComponent<PlayerRoleInfo>();
        Survivor = GetComponent<SurvivorState>();       // Hunter không có, null
        Interactor = GetComponent<PlayerInteractor>();  // Hunter có thể null
        Pallet = GetComponent<PalletInteract>();
        Vault = GetComponent<PlayerVault>();
        Carry = GetComponent<HunterCarry>();            // Survivor không có, null
        Attack = GetComponent<HunterAttack>();          // Survivor không có, null

        character = CharacterInput.Of(gameObject);

        Pad = GetComponent<BotInput>();
        if (Pad == null) Pad = gameObject.AddComponent<BotInput>();
        Nav = GetComponent<BotNavigator>();
        if (Nav == null) Nav = gameObject.AddComponent<BotNavigator>();
    }

    /// <summary>
    /// SERVER gọi khi người chơi rớt mạng: đứng im frozenSeconds giây rồi bot tiếp quản.
    /// Phải gọi SAU khi ChangeOwnership(ServerClientId) để IsOwner = true trên server.
    /// </summary>
    public void BeginStandIn(ulong originalClientId)
    {
        OriginalClientId = originalClientId;
        ActiveBots[originalClientId] = this;

        var cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = Move == null || Move.ShouldControllerBeOn();

        State = BotState.Frozen;
        frozenLeft = frozenSeconds;
        Pad.Move = Vector2.zero;
        Nav.Stop();
        character.UseIdle();
        BotLog.Write(Who, "state", $"Frozen ({frozenSeconds:F0}s)");
    }

    /// <summary>Tự lấy OwnerClientId khi gọi không tham số (dành cho debug hotkey).</summary>
    public void BeginStandIn() => BeginStandIn(OwnerClientId);

    /// <summary>Bot tiếp quản ngay.</summary>
    public void StartBot()
    {
        if (OriginalClientId == 0 && netObj != null)
            OriginalClientId = netObj.OwnerClientId;
        ActiveBots[OriginalClientId] = this;

        bool hunter = RoleInfo != null && RoleInfo.CurrentRole == Role.Hunter;
        brain = hunter ? (BotBrain)new HunterBrain(this) : new SurvivorBrain(this);
        State = BotState.Bot;
        nextThink = 0f;
        character.Use(Pad);
        BotLog.Write(Who, "state", hunter ? "Bot (Hunter)" : "Bot (Survivor)");
    }

    /// <summary>Người chơi quay lại / tắt bot: trả nhân vật về cho người điều khiển.</summary>
    public void Release()
    {
        ActiveBots.Remove(OriginalClientId);
        State = BotState.Off;
        brain = null;
        Pad.Move = Vector2.zero;
        Nav.Stop();
        character.UseHuman();
        BotLog.Write(Who, "state", "Off (trả lại cho người chơi)");
    }

    void OnDestroy()
    {
        if (State != BotState.Off)
            ActiveBots.Remove(OriginalClientId);
    }

    void Update()
    {
        if (State == BotState.Off) return;
        if (netObj == null || !netObj.IsSpawned || !netObj.IsOwner) return;   // chỉ owner (= server khi bot) điều khiển

        if (State == BotState.Frozen)
        {
            Pad.Move = Vector2.zero;
            frozenLeft -= Time.deltaTime;
            if (frozenLeft <= 0f) StartBot();
            return;
        }

        if (Time.time >= nextThink)
        {
            nextThink = Time.time + thinkInterval;
            try { brain.Think(); }
            catch (System.Exception e)
            {
                // Một lần Think lỗi (null...) không được làm hỏng cả khung hình, nếu không bot sẽ đứng im mãi. Log tối đa 5 giây/lần.
                if (Time.time - lastThinkError > 5f) { lastThinkError = Time.time; Debug.LogException(e, this); }
                BotLog.Write(Who, "error", e.GetType().Name + ": " + e.Message);
            }
        }

        bool locked = Move != null && Move.IsLocked;   // đang đánh, bế, bị choáng... thì đứng yên
        Pad.Move = locked ? Vector2.zero : Nav.Steer();
    }

    void OnGUI()
    {
        if (!showDebugLabel || State == BotState.Off) return;
        if (netObj == null || !netObj.IsSpawned || !netObj.IsOwner) return;

        int idx = 0;
        foreach (var kv in ActiveBots)
        {
            if (kv.Value == this) break;
            idx++;
        }

        string text = State == BotState.Frozen
            ? $"BOT ({Who}): đứng im {Mathf.CeilToInt(frozenLeft)}s rồi tiếp quản"
            : $"BOT ({Who}): {Goal}   [{Nav.Mode}]";
        GUI.Label(new Rect(12f, 12f + idx * 24f, 600f, 24f), text);
    }
}
