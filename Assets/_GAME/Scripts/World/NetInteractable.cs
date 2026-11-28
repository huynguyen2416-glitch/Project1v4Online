using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lá»›p cha cho má»i thá»© Survivor nháº¥n F Ä‘á»ƒ lÃ m: mÃ¡y mÃ£ hÃ³a, má»Ÿ cá»•ng, nháº£y háº§m.
/// Nháº¥n F má»™t láº§n lÃ  thanh tiáº¿n Ä‘á»™ tá»± cháº¡y; Ä‘i xa / di chuyá»ƒn / nháº¥n F láº§n ná»¯a thÃ¬ buÃ´ng (tiáº¿n Ä‘á»™ Ä‘Æ°á»£c giá»¯).
/// RiÃªng thá»© nÃ o Ä‘áº·t IsInstant = true (nháº£y háº§m) thÃ¬ nháº¥n F lÃ  xong ngay, khÃ´ng cÃ³ thanh tiáº¿n Ä‘á»™.
/// Client chá»‰ gá»­i "báº¯t Ä‘áº§u / dá»«ng"; SERVER kiá»ƒm tra vai, tráº¡ng thÃ¡i, khoáº£ng cÃ¡ch rá»“i má»›i cá»™ng tiáº¿n Ä‘á»™.
/// </summary>
public abstract class NetInteractable : NetworkBehaviour
{
    [SerializeField] protected float duration = 20f;   // giÃ¢y náº¿u 1 ngÆ°á»i lÃ m; nhiá»u ngÆ°á»i thÃ¬ nhanh hÆ¡n
    [SerializeField] float range = 2.5f;
    [SerializeField] protected Renderer visual;                   // Ä‘á»ƒ trá»‘ng thÃ¬ tá»± tÃ¬m Renderer

    [Header("Cháº·n xuyÃªn tÆ°á»ng (chá»‰ cÃ³ tÃ¡c dá»¥ng vá»›i thá»© báº­t RequireLineOfSight, vd. mÃ¡y mÃ£ hÃ³a)")]
    [Tooltip("Äá»™ cao Ä‘iá»ƒm báº¯n tia kiá»ƒm tra tÆ°á»ng (ngang ngá»±c), tÃ­nh tá»« chÃ¢n ngÆ°á»i chÆ¡i / tá»« gá»‘c cá»§a váº­t.")]
    [SerializeField] float losEyeHeight = 1.0f;
    [Tooltip("Layer Ä‘Æ°á»£c coi lÃ  váº­t cáº£n. Bá» tick layer nÃ o khÃ´ng muá»‘n cháº·n.")]
    [SerializeField] LayerMask losMask = ~0;

    public readonly NetworkVariable<float> Progress = new NetworkVariable<float>(0f);
    public readonly NetworkVariable<bool> Done = new NetworkVariable<bool>(false);
    public readonly NetworkVariable<bool> InUse = new NetworkVariable<bool>(false);

    readonly HashSet<ulong> users = new HashSet<ulong>();
    readonly List<ulong> toRemove = new List<ulong>();
    /// <summary>Nhá»¯ng ngÆ°á»i Ä‘ang lÃ m lÃºc hoÃ n thÃ nh (dÃ¹ng trong OnCompleted).</summary>
    protected readonly List<ulong> finishers = new List<ulong>();
    float serverProgress;

    /// <summary>Má»i NetInteractable Ä‘ang spawn, Ä‘á»ƒ khá»i pháº£i FindObjectsByType má»—i láº§n quÃ©t.</summary>
    public static readonly List<NetInteractable> All = new List<NetInteractable>();

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static MaterialPropertyBlock colorBlock;
    Color lastColor;
    bool hasColor;

    public float Range => range;
    public abstract string PromptText { get; }
    public virtual bool IsAvailable => !Done.Value;
    /// <summary>true = pháº£i khÃ´ng cÃ³ tÆ°á»ng cháº¯n giá»¯a ngÆ°á»i chÆ¡i vÃ  váº­t nÃ y má»›i lÃ m Ä‘Æ°á»£c (mÃ¡y mÃ£ hÃ³a).</summary>
    public virtual bool RequireLineOfSight => false;

    /// <summary>KhÃ´ng bá»‹ tÆ°á»ng cháº¯n giá»¯a actorPos (chÃ¢n ngÆ°á»i chÆ¡i) vÃ  váº­t nÃ y. Váº­t khÃ´ng báº­t RequireLineOfSight thÃ¬ luÃ´n true.</summary>
    public bool HasLineOfSight(Vector3 actorPos)
    {
        if (!RequireLineOfSight) return true;
        return LineOfSight.Clear(actorPos, transform.position, losEyeHeight, losMask, transform);
    }
    /// <summary>true = nháº¥n F lÃ  xong ngay, khÃ´ng cÃ³ thanh tiáº¿n Ä‘á»™ (vÃ­ dá»¥ nháº£y háº§m).</summary>
    public virtual bool IsInstant => false;
    /// <summary>Server há»i trÆ°á»›c khi cho userId lÃ m (vÃ­ dá»¥ cháº·n tá»± há»“i mÃ¡u cho chÃ­nh mÃ¬nh). Máº·c Ä‘á»‹nh cho phÃ©p.</summary>
    public virtual bool CanUse(ulong userId) => true;
    /// <summary>Server: userId Ä‘ang lÃ m thá»© nÃ y khÃ´ng? Terror Shock dÃ¹ng Ä‘á»ƒ biáº¿t Survivor Ä‘ang há»“i mÃ¡u hoáº·c cá»©u ngÆ°á»i.</summary>
    public bool IsUsedBy(ulong clientId) => users.Contains(clientId);
    /// <summary>NgÆ°á»i Ä‘ang lÃ m thá»© nÃ y bá»‹ Hunter Ä‘Ã¡nh trÃºng thÃ¬ Gá»¥c ngay (Terror Shock). Lá»›p con báº­t náº¿u cáº§n.</summary>
    public virtual bool TerrorShockOnUse => false;
    protected virtual void OnCompleted() { }
    /// <summary>Server: cÃ³ ngÆ°á»i vá»«a báº¯t Ä‘áº§u lÃ m. Lá»›p con ghi Ä‘Ã¨ náº¿u cáº§n (gháº¿ tÃªn lá»­a: khÃ³a ngÆ°á»i cá»©u).</summary>
    protected virtual void OnUserAdded(ulong id) { }
    /// <summary>Server: cÃ³ ngÆ°á»i vá»«a buÃ´ng (Ä‘i xa, nháº¥n F láº§n ná»¯a, bá»‹ Ä‘Ã¡nh, rá»›t máº¡ng...). KhÃ´ng gá»i khi lÃ m xong.</summary>
    protected virtual void OnUserRemoved(ulong id) { }
    protected virtual Color GetColor() => Color.Lerp(Color.red, Color.green, Progress.Value);

    public override void OnNetworkSpawn() { All.Add(this); }
    public override void OnNetworkDespawn() { All.Remove(this); }

    /// <summary>Khoáº£ng cÃ¡ch trÃªn máº·t pháº³ng ngang (bá» qua chÃªnh lá»‡ch Ä‘á»™ cao).</summary>
    public float FlatDistance(Vector3 p)
    {
        Vector3 d = p - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    [ServerRpc(RequireOwnership = false)]
    public void StartUseServerRpc(ServerRpcParams p = default)
    {
        if (MatchManager.Ended) return;
        ulong id = p.Receive.SenderClientId;
        if (!IsAvailable || !CanUse(id)) return;
        if (!TryGetSurvivor(id, out Transform t)) return;
        if (FlatDistance(t.position) > range + 0.5f) return;   // dung sai cho Ä‘á»™ trá»…
        if (!HasLineOfSight(t.position)) return;                // bá»‹ tÆ°á»ng cháº¯n
        if (IsInstant) { CompleteInstantly(id); return; }
        if (users.Add(id)) OnUserAdded(id);
    }

    /* â•â•â•â•â•â•â•â•â•â•â•â• Server Direct (Bot dÃ¹ng, bypass RPC) â•â•â•â•â•â•â•â•â•â•â•â• */

    /// <summary>Bot trÃªn server gá»i trá»±c tiáº¿p, khÃ´ng qua RPC. DÃ¹ng OriginalClientId.</summary>
    public void ServerStartUse(ulong originalClientId, Transform actor)
    {
        if (!IsServer || MatchManager.Ended) return;
        if (!IsAvailable || !CanUse(originalClientId)) return;
        if (actor == null || FlatDistance(actor.position) > range + 0.5f) return;
        if (!HasLineOfSight(actor.position)) return;
        if (IsInstant) { CompleteInstantly(originalClientId); return; }
        if (users.Add(originalClientId)) OnUserAdded(originalClientId);
    }

    /// <summary>Bot trÃªn server gá»i trá»±c tiáº¿p.</summary>
    public void ServerStopUse(ulong originalClientId)
    {
        if (!IsServer) return;
        if (users.Remove(originalClientId)) OnUserRemoved(originalClientId);
    }

    /// <summary>
    /// ÄÆ°a tiáº¿n Ä‘á»™ vá» 0 Ä‘á»ƒ dÃ¹ng láº¡i (gháº¿ tÃªn lá»­a, há»“i mÃ¡u). keepFinishers = true: nhá»¯ng ngÆ°á»i vá»«a lÃ m xong váº«n tiáº¿p tá»¥c lÃ m.
    /// Gá»i trong OnCompleted.
    /// </summary>
    protected void ServerResetProgress(bool keepFinishers = false)
    {
        serverProgress = 0f;
        Progress.Value = 0f;
        Done.Value = false;
        if (keepFinishers) foreach (ulong id in finishers) users.Add(id);
    }

    /// <summary>HoÃ n thÃ nh ngay trÃªn server cho thá»© IsInstant (khÃ´ng qua thanh tiáº¿n Ä‘á»™).</summary>
    void CompleteInstantly(ulong id)
    {
        if (Done.Value) return;
        serverProgress = 1f;
        Progress.Value = 1f;
        Done.Value = true;
        finishers.Clear();
        finishers.Add(id);
        OnCompleted();
    }

    [ServerRpc(RequireOwnership = false)]
    public void StopUseServerRpc(ServerRpcParams p = default)
    {
        if (users.Remove(p.Receive.SenderClientId)) OnUserRemoved(p.Receive.SenderClientId);
    }

    /// <summary>
    /// Láº¥y Survivor cá»§a clientId náº¿u há»£p lá»‡. Máº·c Ä‘á»‹nh pháº£i cÃ²n hÃ nh Ä‘á»™ng Ä‘Æ°á»£c (Khá»e/Bá»‹ thÆ°Æ¡ng).
    /// allowDowned = true: cho cáº£ ngÆ°á»i Ä‘ang Gá»¥c (dÃ¹ng khi kiá»ƒm tra thoÃ¡t qua cá»•ng, vÃ¬ ngÆ°á»i gá»¥c váº«n bÃ² ra cá»•ng Ä‘Æ°á»£c).
    /// Há»— trá»£ bot: náº¿u clientId khÃ´ng cÃ²n trong ConnectedClients, tÃ¬m trong BotController.ActiveBots.
    /// </summary>
    protected static bool TryGetSurvivor(ulong clientId, out Transform t, bool allowDowned = false)
    {
        t = null;
        NetworkManager nm = NetworkManager.Singleton;

        // Thá»­ tá»« ConnectedClients trÆ°á»›c (ngÆ°á»i chÆ¡i Ä‘ang káº¿t ná»‘i)
        if (nm != null && nm.ConnectedClients.TryGetValue(clientId, out var c) && c.PlayerObject != null)
        {
            PlayerRoleInfo info = c.PlayerObject.GetComponent<PlayerRoleInfo>();
            if (info == null || info.CurrentRole != Role.Survivor) return false;

            SurvivorState st = c.PlayerObject.GetComponent<SurvivorState>();
            if (st != null)
            {
                bool ok = st.IsActive || (allowDowned && st.Current == SurvivorHealth.Downed);
                if (!ok) return false;
            }

            t = c.PlayerObject.transform;
            return true;
        }

        // Fallback: tÃ¬m trong bot Ä‘ang hoáº¡t Ä‘á»™ng (client Ä‘Ã£ disconnect, bot Ä‘ang thay)
        if (BotController.ActiveBots.TryGetValue(clientId, out BotController bot) && bot != null && bot.NetObj != null)
        {
            PlayerRoleInfo info = bot.RoleInfo;
            if (info == null || info.CurrentRole != Role.Survivor) return false;

            SurvivorState st = bot.Survivor;
            if (st != null)
            {
                bool ok = st.IsActive || (allowDowned && st.Current == SurvivorHealth.Downed);
                if (!ok) return false;
            }

            t = bot.transform;
            return true;
        }

        return false;
    }

   

    protected virtual void Update()
    {
        if (!IsServer) return;

        bool active = !Done.Value && users.Count > 0;
        if (InUse.Value != active) InUse.Value = active;

        if (Done.Value || users.Count == 0) return;

        toRemove.Clear();
        foreach (ulong id in users)
        {
            if (!IsAvailable || !CanUse(id) || !TryGetSurvivor(id, out Transform t) || FlatDistance(t.position) > range + 0.5f || !HasLineOfSight(t.position))
                toRemove.Add(id);   // Ä‘i xa / rá»›t máº¡ng / bá»‹ Ä‘Ã¡nh / háº¿t hiá»‡u lá»±c -> buÃ´ng
        }
        foreach (ulong id in toRemove) { if (users.Remove(id)) OnUserRemoved(id); }
        if (users.Count == 0)
        {
            if (InUse.Value) InUse.Value = false;
            return;
        }

        float speed = users.Count switch { 1 => 1f, 2 => 1.6f, 3 => 2.0f, _ => 2.2f };
        serverProgress = Mathf.Min(1f, serverProgress + speed * Time.deltaTime / duration);
        if (serverProgress - Progress.Value >= 0.01f || serverProgress >= 1f)
            Progress.Value = serverProgress;   // ghi theo bước 1% để đỡ tốn băng thông

        if (serverProgress >= 1f)
        {
            Done.Value = true;
            InUse.Value = false;
            finishers.Clear();
            finishers.AddRange(users);
            users.Clear();
            OnCompleted();
        }
    }
}
