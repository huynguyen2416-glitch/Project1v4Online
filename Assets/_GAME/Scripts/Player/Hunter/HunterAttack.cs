using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Đòn đánh của Hunter. Việc bế / thả / đặt lên ghế nằm ở HunterCarry.
/// Nhịp đòn: bấm -> vung (hitDelay) -> server mới tính trúng/hụt -> lau kiếm.
/// Hitbox là hình quạt trước mặt (có tính bề ngang thân Survivor), bị tường chắn, khác tầng thì không trúng.
/// </summary>
[RequireComponent(typeof(PlayerRoleInfo))]
[RequireComponent(typeof(NetworkPlayerMovement))]
public class HunterAttack : NetworkBehaviour
{
    // Server nhả khóa sớm 10% để bù độ trễ mạng, tránh từ chối nhầm yêu cầu kế tiếp của owner.
    public const float ServerLockSlack = 0.9f;


    [Header("Hitbox: hình quạt trước mặt (chọn Hunter trong Scene để xem hình quạt màu đỏ)")]
    [Tooltip("Tầm với (mét), tính từ tâm Hunter tới MÉP thân Survivor.")]
    [SerializeField] float reach = 1.5f;
    [Range(10f, 180f)][SerializeField] float halfAngle = 60f;
    [Tooltip("Bán kính thân Survivor: để người đứng lệch mép quạt nhưng thân còn chạm vẫn trúng.")]
    [SerializeField] float bodyRadius = 0.35f;
    [Tooltip("Chênh lệch độ cao tối đa để còn trúng (khác tầng thì không trúng).")]
    [SerializeField] float maxHeightDiff = 1.5f;

    [Header("Nhịp đòn đánh")]
    [Tooltip("Từ lúc bấm đến lúc đòn thật sự tính trúng (giây). Chỉnh cho khớp frame vung tới của animation. Phải nhỏ hơn Lock On Miss.")]
    [SerializeField] float hitDelay = 0.3f;
    [SerializeField] float cooldown = 1.2f;

    [Header("Vật cản")]
    [Tooltip("Độ cao điểm bắn tia kiểm tra tường (ngang ngực), tính từ chân.")]
    [SerializeField] float eyeHeight = 1.0f;
    [Tooltip("Layer được coi là vật cản. Bỏ tick layer nào không muốn chặn đòn.")]
    [SerializeField] LayerMask blockMask = ~0;

    [Header("Khóa di chuyển sau khi đánh (giây, tính từ lúc bấm). 0 = không khóa. Chỉnh cho bằng độ dài animation.")]
    [SerializeField] float lockOnHit = 4.5f;     // đánh trúng: vung + lau kiếm
    [SerializeField] float lockOnMiss = 0.8f;    // đánh hụt
    [SerializeField] float recoveryDelay = 0.3f; // từ lúc bấm tới lúc chạy animation lau kiếm

    [Header("Animation")]
    [SerializeField] Animator animator;

    static readonly int AttackParam = Animator.StringToHash("attack");
    static readonly int AttackRecoveryParam = Animator.StringToHash("attack_recovery");
    static readonly int StunnedParam = Animator.StringToHash("isStunned");
    bool hasStunned;
    Coroutine stunRoutine;
    PlayerRoleInfo role;
    NetworkPlayerMovement move;
    HunterCarry carry;
    BotController botCtrl;
    bool hasAttack, hasRecovery;

    float serverNextAttack;
    float nextLocalAttack;

    /// <summary>Server: Hunter còn đang làm động tác (đánh, bế, thả...) đến thời điểm này thì chặn mọi yêu cầu mới.</summary>
    float serverBusyUntil;

    /// <summary>Server: tăng mỗi khi có nơi khác gọi ServerBeginAction (choáng pallet, phá pallet...) -> hủy đòn đang vung.</summary>
    int actionVersion;

    CharacterInput input;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        input = CharacterInput.Of(gameObject);
        move = GetComponent<NetworkPlayerMovement>();
        carry = GetComponent<HunterCarry>();
        botCtrl = GetComponent<BotController>();
        if (animator == null) animator = GetComponentInChildren<Animator>();

        if (animator != null)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.nameHash == AttackParam) hasAttack = true;
                if (p.nameHash == AttackRecoveryParam) hasRecovery = true;
                if (p.nameHash == StunnedParam) hasStunned = true;
            }
        }
    }

    /// <summary>Server: đây có phải Hunter hợp lệ, trận chưa kết thúc và đang rảnh tay không.</summary>
    public bool ServerCanAct =>
        IsServer && role.CurrentRole == Role.Hunter && !MatchManager.Ended && Time.time >= serverBusyUntil;

    /// <summary>Server: đánh dấu Hunter bận seconds giây (có trừ hao độ trễ). Gọi từ nơi khác cũng hủy đòn đang vung.</summary>
    public void ServerBeginAction(float seconds)
    {
        actionVersion++;
        SetBusy(seconds);
    }

    void SetBusy(float seconds) => serverBusyUntil = Time.time + seconds * ServerLockSlack;

    /// <summary>Bot đang hoạt động trên server?</summary>
    bool IsBotActive => IsServer && botCtrl != null && botCtrl.State != BotController.BotState.Off;

    void Update()
    {
        if (!IsSpawned || role.CurrentRole != Role.Hunter) return;
        if (!IsOwner && !IsBotActive) return;
        if (move.IsLocked) return;
        if (carry != null && carry.IsCarrying) return;

        if (input.AttackDown && Time.time >= nextLocalAttack)
        {
            nextLocalAttack = Time.time + cooldown;

            // Bot trên server → gọi logic trực tiếp, không qua RPC
            if (IsBotActive)
                ServerPerformAttack();
            else
                AttackServerRpc(NetworkManager.ServerTime.Time);
        }
    }

    /// <summary>Server thực hiện đòn đánh (dùng chung cho RPC và bot direct).</summary>
    void ServerPerformAttack()
    {
        if (!ServerCanAct || (carry != null && carry.IsCarrying)) return;
        if (Time.time < serverNextAttack) return;
        serverNextAttack = Time.time + cooldown * ServerLockSlack;

        float delay = Mathf.Min(hitDelay, lockOnMiss * 0.9f);
        SetBusy(lockOnMiss);
        int version = actionVersion;

        PlayAttackStartClientRpc();
        StartCoroutine(ResolveAttack(delay, version));
    }

    [ServerRpc]
    void AttackServerRpc(double clientSendTime)
    {
        // Số đo hit delay (câu 5): từ lúc client bấm đánh đến lúc server nhận được. Chỉ ghi log, không ảnh hưởng luật chơi.
        NetMetricsHub.RaiseHitDelay(OwnerClientId, (float)((NetworkManager.ServerTime.Time - clientSendTime) * 1000.0));
        ServerPerformAttack();
    }

    IEnumerator ResolveAttack(float delay, int version)
    {
        yield return new WaitForSeconds(delay);
        if (!IsSpawned || version != actionVersion || MatchManager.Ended || role.CurrentRole != Role.Hunter) yield break;

        SurvivorState target = FindHitTarget();
        bool isHit = target != null && target.ServerHit();
        SetBusy(Mathf.Max(0f, (isHit ? lockOnHit : lockOnMiss) - delay));

        ResolveAttackClientRpc(isHit, delay);
    }

    /// <summary>Server: Survivor đang trúng hình quạt, không bị tường chắn, cùng tầng; chọn người gần nhất.</summary>
    SurvivorState FindHitTarget()
    {
        SurvivorState best = null;
        float bestDist = float.MaxValue;

        foreach (SurvivorState st in SurvivorState.All)
        {
            if (!st.IsActive) continue;

            Vector3 to = st.transform.position - transform.position;
            if (Mathf.Abs(to.y) > maxHeightDiff) continue;           // khác tầng
            to.y = 0f;
            float d = to.magnitude;
            if (d - bodyRadius > reach) continue;                    // xa quá (tính tới mép thân)

            if (d > bodyRadius)                                      // đứng sát/chồng lên thì không xét góc
            {
                float angle = Vector3.Angle(transform.forward, to);
                float bodyAngle = Mathf.Rad2Deg * Mathf.Asin(Mathf.Clamp01(bodyRadius / d));   // góc che bởi bề ngang thân
                if (angle - bodyAngle > halfAngle) continue;
            }

            if (!LineOfSight.Clear(transform.position, st.transform.position, eyeHeight, blockMask)) continue;   // bị tường chắn

            if (d < bestDist) { bestDist = d; best = st; }
        }
        return best;
    }

    [ClientRpc]
    void PlayAttackStartClientRpc()
    {
        if (animator != null && hasAttack) animator.SetTrigger(AttackParam);
        if (IsOwner) move.LockFor(lockOnMiss);
    }

    [ClientRpc]
    void ResolveAttackClientRpc(bool isHit, float delay)
    {
        if (!isHit) return;
        if (IsOwner) move.LockFor(Mathf.Max(0f, lockOnHit - delay));   // LockFor lấy giá trị lớn hơn nên chỉ kéo dài khóa
        StartCoroutine(PlayRecoveryAfterDelay(Mathf.Max(0f, recoveryDelay - delay)));
    }
    /// <summary>Server: phát animation choáng trên mọi máy trong seconds giây (pallet rơi trúng Hunter).</summary>
    public void ServerPlayStun(float seconds)
    {
        if (!IsServer) return;
        PlayStunClientRpc(seconds);
    }

    [ClientRpc]
    void PlayStunClientRpc(float seconds)
    {
        if (animator == null || !hasStunned) return;
        if (stunRoutine != null) StopCoroutine(stunRoutine);
        animator.SetBool(StunnedParam, true);
        stunRoutine = StartCoroutine(EndStunAfter(seconds));
    }

    IEnumerator EndStunAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        animator.SetBool(StunnedParam, false);
        stunRoutine = null;
    }

    IEnumerator PlayRecoveryAfterDelay(float wait)
    {
        if (wait > 0f) yield return new WaitForSeconds(wait);
        if (animator != null && hasRecovery) animator.SetTrigger(AttackRecoveryParam);
    }

    /// <summary>Vẽ hình quạt hitbox khi chọn Hunter trong Scene view, để chỉnh reach / halfAngle bằng mắt.</summary>
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.9f);
        Vector3 p = transform.position + Vector3.up * 0.1f;
        float r = reach + bodyRadius;   // xa nhất mà TÂM Survivor còn trúng
        const int steps = 24;

        Vector3 prev = p + Quaternion.Euler(0f, -halfAngle, 0f) * transform.forward * r;
        Gizmos.DrawLine(p, prev);
        for (int i = 1; i <= steps; i++)
        {
            float a = -halfAngle + 2f * halfAngle * i / steps;
            Vector3 next = p + Quaternion.Euler(0f, a, 0f) * transform.forward * r;
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
        Gizmos.DrawLine(p, prev);
    }
}
