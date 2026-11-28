using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Pallet (kiểu Dead by Daylight / Identity V). Phím tương tác: SPACE (xem PalletInteract).
///
///   Standing  → Survivor nhấn Space → Dropped (tức thì + chạy anim "Drop") và choáng Hunter đang đứng trong vùng.
///   Dropped   → Survivor nhấn Space → trèo qua (giống cửa sổ).
///             → Hunter nhấn Space   → phá (anim "Break" ~1.5 giây) → Destroyed.
///   Destroyed → không còn gì.
///
/// VÙNG TƯƠNG TÁC giống hệt VaultWindow: hộp chữ nhật rộng "width" (trục X) và sâu "approachDepth" mỗi phía (trục Z),
/// người chơi phải quay mặt về phía bên kia pallet. Chọn pallet trong Scene view sẽ thấy hộp vàng và 2 chấm xanh (điểm đáp).
///
/// SETUP PREFAB:
///   Root: NetworkObject, Pallet.cs, Animator (trigger "Drop", "Break"). Trục XANH (Z) của root phải đi XUYÊN qua pallet
///   (hướng người chạy qua). Chỉ xoay quanh trục Y, Scale (1, 1, 1).
///   blockCollider: BoxCollider (không tick Is Trigger) chặn đường khi pallet nằm xuống. Script tự bật/tắt.
///   standingVisual / droppedVisual: 2 child hiển thị pallet đứng / nằm.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Pallet : NetworkBehaviour
{
    /// <summary>Mọi pallet trong scene. PalletInteract dùng để quét.</summary>
    public static readonly List<Pallet> All = new List<Pallet>();

    /// <summary>Server cho phép người chơi đứng lệch thêm chừng này (mét) so với vùng, bù độ trễ mạng.</summary>
    const float ServerRangeMargin = 1f;

    [Header("Visuals & Animator")]
    [Tooltip("Child hiển thị pallet dựng thẳng")]
    [SerializeField] GameObject standingVisual;
    [Tooltip("Child hiển thị pallet nằm xuống")]
    [SerializeField] GameObject droppedVisual;
    [Tooltip("Animator trên root prefab. Trigger: 'Drop', 'Break'")]
    [SerializeField] Animator animator;

    [Header("Collider")]
    [Tooltip("BoxCollider (KHÔNG trigger) chặn di chuyển khi Dropped. Bật/tắt tự động.")]
    [SerializeField] Collider blockCollider;

    [Header("Vùng tương tác (giống VaultWindow)")]
    [Tooltip("Bề ngang vùng đứng được (dọc trục X), mét.")]
    [SerializeField] float width = 2.4f;
    [Tooltip("Đứng cách tâm pallet tối đa bao nhiêu mét (dọc trục Z) thì được đập / trèo / phá.")]
    [SerializeField] float approachDepth = 1.5f;

    [Header("Kích thước pallet")]
    [Tooltip("Nửa chiều sâu pallet theo trục Z (mét). Dùng để đẩy Hunter ra đúng phía khi bị choáng. Chỉnh khớp mesh.")]
    [SerializeField] float halfDepth = 0.7f;

    [Header("Choáng Hunter lúc pallet rơi")]
    [SerializeField] float stunRadius = 2.2f;
    [SerializeField] float stunDuration = 2.5f;

    [Header("Trèo qua (Survivor)")]
    [Tooltip("Điểm đáp cách tâm pallet bao xa ở phía bên kia.")]
    [SerializeField] float vaultLanding = 1.3f;
    [SerializeField] float vaultArcHeight = 0.2f;
    [Tooltip("Thời gian trèo (giây), chỉnh khớp clip animation.")]
    [SerializeField] float vaultDuration = 0.55f;

    [Header("Phá pallet (Hunter)")]
    [Tooltip("Thời gian animation phá chạy trước khi pallet biến mất (giây). Hunter bị khóa trong lúc này.")]
    [SerializeField] float breakAnimDuration = 1.5f;

    [Header("Animation tự động (không cần clip, không cần Animator)")]
    [Tooltip("Bật: pallet tự hạ xuống (xoay quanh mép) rồi khi bị phá thì rung và vỡ thành mảnh. Tắt: dùng Animator như cũ.")]
    [SerializeField] bool proceduralAnimation = true;
    [Tooltip("Thời gian pallet đổ xuống (giây).")]
    [SerializeField] float dropDuration = 0.15f;
    [Tooltip("Góc đổ (độ). Chỉnh cho gần khớp tư thế của 'Dropped Visual' để lúc đổi mô hình không bị giật.")]
    [SerializeField] float dropAngle = 85f;
    [Tooltip("Mép bản lề cách tâm pallet bao xa (mét, dọc trục Z) về phía Survivor. Chỉnh cho đến khi pallet đổ trông tự nhiên.")]
    [SerializeField] float hingeOffset = 0.35f;
    [Tooltip("Đổi chiều đổ nếu pallet đổ về phía Survivor thay vì phía Hunter.")]
    [SerializeField] bool invertFallDirection = false;
    [Tooltip("Độ nảy nhẹ khi chạm đất (độ).")]
    [SerializeField] float dropBounce = 4f;
    [Tooltip("Số mảnh vỡ văng ra khi pallet bị phá.")]
    [SerializeField] int debrisCount = 14;
    [Tooltip("Thời gian mảnh vỡ tồn tại (giây).")]
    [SerializeField] float debrisLifetime = 1.1f;
    [Tooltip("Tùy chọn: hiệu ứng bụi phát khi pallet chạm đất.")]
    [SerializeField] ParticleSystem dropDust;
    [Tooltip("Tùy chọn: âm thanh pallet đổ / bị phá.")]
    [SerializeField] AudioClip dropSound;
    [SerializeField] AudioClip breakSound;

    // ── NetworkVariables ──────────────────────────────────────────────────────

    /// <summary>Hướng đổ: +1 = về phía +Z của pallet, -1 = phía -Z. Server ghi TRƯỚC State để mọi máy đọc kịp khi State đổi.</summary>
    public readonly NetworkVariable<sbyte> FallSign = new NetworkVariable<sbyte>(1);

    public readonly NetworkVariable<byte> State =
        new NetworkVariable<byte>((byte)PalletState.Standing);

    /// <summary>True khi Hunter đang phá (animation chạy). Mọi máy đọc để chặn trèo qua pallet đang vỡ.</summary>
    public readonly NetworkVariable<bool> IsBreaking = new NetworkVariable<bool>(false);

    public PalletState CurrentState => (PalletState)State.Value;
    public bool IsStanding => CurrentState == PalletState.Standing;
    public bool IsDropped => CurrentState == PalletState.Dropped;
    public bool IsDestroyed => CurrentState == PalletState.Destroyed;

    public float VaultLanding => vaultLanding;
    public float VaultDuration => vaultDuration;
    public float VaultArcHeight => vaultArcHeight;
    public float BreakDuration => breakAnimDuration;

    // ── Hình học (giống VaultWindow) ──────────────────────────────────────────

    /// <summary>Hướng đi xuyên pallet (nằm ngang, độ dài 1).</summary>
    Vector3 Forward
    {
        get
        {
            Vector3 f = transform.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
        }
    }

    Vector3 Right => Vector3.Cross(Vector3.up, Forward);

    /// <summary>
    /// Người chơi có đang đứng trong vùng không? side = +1 nếu đứng phía +Z, -1 nếu phía -Z.
    /// margin cho phép đứng lệch thêm (server dùng để bù độ trễ).
    /// KHÔNG kiểm tra hướng nhìn — Survivor không cần quay mặt vào pallet.
    /// </summary>
    public bool TryGetSide(Vector3 worldPos, out int side, out float distance, float margin = 0f)
    {
        Vector3 d = worldPos - transform.position;
        d.y = 0f;
        float x = Vector3.Dot(d, Right);
        float z = Vector3.Dot(d, Forward);
        side = z >= 0f ? 1 : -1;
        distance = d.magnitude;
        return Mathf.Abs(x) <= width * 0.5f + margin && Mathf.Abs(z) <= approachDepth + margin;
    }

    /// <summary>Hướng nhảy khi đứng ở phía fromSide (đi sang phía đối diện).</summary>
    public Vector3 JumpDirection(int fromSide) => -fromSide * Forward;

    /// <summary>Điểm đáp ở phía bên kia, giữ nguyên độ cao y của người chơi.</summary>
    public Vector3 GetLandingPoint(int fromSide, float y)
    {
        Vector3 p = transform.position + JumpDirection(fromSide) * vaultLanding;
        p.y = y;
        return p;
    }

    // ── Server-only ───────────────────────────────────────────────────────────

    bool breakInProgress;
    float breakTimer;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        All.Add(this);
        shattered = false;
        DisableProceduralAnimators();
        State.OnValueChanged += OnStateChanged;
        IsBreaking.OnValueChanged += OnBreakingChanged;
        ApplyCollider(CurrentState);
        ApplyVisuals(CurrentState);   // vào giữa chừng: hiện thẳng trạng thái hiện tại, không chạy animation
    }

    void DisableProceduralAnimators()
    {
        if (!proceduralAnimation) return;
        if (animator != null) animator.enabled = false;
        if (standingVisual != null)
        {
            foreach (Animator a in standingVisual.GetComponentsInChildren<Animator>(true))
                a.enabled = false;
        }
        if (droppedVisual != null)
        {
            foreach (Animator a in droppedVisual.GetComponentsInChildren<Animator>(true))
                a.enabled = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        State.OnValueChanged -= OnStateChanged;
        IsBreaking.OnValueChanged -= OnBreakingChanged;
        StopAnimations();
    }

    void OnStateChanged(byte prev, byte next)
    {
        PalletState p = (PalletState)prev, n = (PalletState)next;
        ApplyCollider(n);   // collider đổi ngay theo trạng thái thật, không đợi animation

        if (n == PalletState.Standing) shattered = false;

        if (proceduralAnimation && n == PalletState.Dropped && p == PalletState.Standing && standingVisual != null)
        {
            if (dropRoutine != null) return;   // người đập đã tự chạy animation đoán trước, server vừa xác nhận
            StopAnimations();
            dropRoutine = StartCoroutine(DropRoutine());
            return;
        }

        // Đang chạy animation vỡ: nó tự tắt hình khi xong, đừng cắt ngang.
        if (n == PalletState.Destroyed && breakRoutine != null) return;

        StopAnimations();
        ApplyVisuals(n);
    }

    void OnBreakingChanged(bool _, bool breaking)
    {
        if (!breaking || !proceduralAnimation) return;
        if (predictingBreak && breakRoutine != null) { predictingBreak = false; return; }   // máy Hunter đã chạy đoán trước, server vừa xác nhận
        StopAnimations();
        breakRoutine = StartCoroutine(BreakRoutine());
    }

    // blockCollider không trigger, chỉ chặn đường khi nằm xuống.
    void ApplyCollider(PalletState s)
    {
        if (blockCollider) blockCollider.enabled = s == PalletState.Dropped;
    }

    void ApplyVisuals(PalletState s)
    {
        if (standingVisual) standingVisual.SetActive(s == PalletState.Standing);
        if (droppedVisual) droppedVisual.SetActive(s == PalletState.Dropped && !shattered);
    }

    // ── Animation tự động ─────────────────────────────────────────────────────

    Coroutine dropRoutine, breakRoutine;
    bool predicting;          // máy người đập: đã chạy animation trước khi server xác nhận
    float predictUntil;       // quá hạn này mà server chưa xác nhận thì hoàn lại pallet đứng
    int predictedFallSign = 1;

    /// <summary>
    /// Máy của người đập gọi NGAY khi bấm phím (cùng lúc gửi yêu cầu lên server) để pallet đổ tức thì, không chờ khứ hồi mạng.
    /// Server vẫn là bên quyết định: bị từ chối thì sau ~0,8 giây pallet tự dựng lại.
    /// </summary>
    public void PredictDrop(int dropperSide)
    {
        if (!proceduralAnimation || standingVisual == null || !IsStanding || dropRoutine != null) return;
        DisableProceduralAnimators();
        predicting = true;
        predictUntil = Time.time + 0.8f;
        predictedFallSign = -dropperSide;
        if (blockCollider) blockCollider.enabled = true;
        StopAnimations();
        dropRoutine = StartCoroutine(DropRoutine());
    }
    bool predictingBreak;     // máy Hunter: đã chạy animation phá trước khi server xác nhận

    /// <summary>
    /// Máy của Hunter gọi NGAY khi bấm phím (cùng lúc gửi yêu cầu lên server) để pallet rung và vỡ tức thì.
    /// Server vẫn quyết định: bị từ chối thì BreakRejectedClientRpc (hoặc đến mốc vỡ mà chưa được xác nhận) sẽ hoàn lại pallet.
    /// </summary>
    public void PredictBreak()
    {
        if (!proceduralAnimation || !IsDropped || IsBreaking.Value || breakRoutine != null) return;
        predictingBreak = true;
        StopAnimations();
        breakRoutine = StartCoroutine(BreakRoutine());
    }

    /// <summary>Bot trên server bị từ chối: hủy animation đoán trước (đường RPC tự hủy qua BreakRejectedClientRpc).</summary>
    public void CancelBreakPrediction() => CancelPredictedBreak();

    /// <summary>Hủy animation phá đoán trước: dừng rung, trả lại tư thế và hình pallet theo trạng thái thật.</summary>
    void CancelPredictedBreak()
    {
        if (!predictingBreak) return;
        predictingBreak = false;
        if (breakRoutine != null) { StopCoroutine(breakRoutine); breakRoutine = null; }
        RestoreVisualPose(droppedVisual != null ? droppedVisual : standingVisual);
        shattered = false;
        ApplyVisuals(CurrentState);
    }

    bool shattered;   // đã vỡ: không bật lại hình pallet nằm dù trạng thái chưa kịp sang Destroyed

    void StopAnimations()
    {
        if (dropRoutine != null) { StopCoroutine(dropRoutine); dropRoutine = null; }
        if (breakRoutine != null) { StopCoroutine(breakRoutine); breakRoutine = null; }
        RestoreVisualPose(standingVisual);
    }

    Vector3 savedPos; Quaternion savedRot; Transform savedTarget;

    void SavePose(Transform t) { savedTarget = t; savedPos = t.localPosition; savedRot = t.localRotation; }

    void RestoreVisualPose(GameObject g)
    {
        if (g != null && savedTarget == g.transform) { savedTarget.localPosition = savedPos; savedTarget.localRotation = savedRot; }
    }

    /// <summary>Pallet đổ: xoay quanh mép dưới, phản hồi ngay ở frame bấm phím rồi nảy nhẹ khi chạm đất, sau đó đổi sang mô hình nằm.</summary>
    IEnumerator DropRoutine()
    {
        Transform t = standingVisual.transform;
        SavePose(t);
        standingVisual.SetActive(true);
        if (droppedVisual) droppedVisual.SetActive(false);

        Vector3 flatForward = Forward;
        float sign = (predicting ? predictedFallSign : FallSign.Value) >= 0 ? 1f : -1f;
        if (invertFallDirection) sign = -sign;
        Vector3 fall = flatForward * sign;                                 // hướng pallet đổ về
        Vector3 hinge = transform.position - fall * hingeOffset;           // mép bản lề, ở sát đất, phía Survivor
        Vector3 axis = Vector3.Cross(Vector3.up, fall);                    // quay quanh trục này thì đỉnh ngả về phía 'fall'
        Vector3 startPos = t.position;
        Quaternion startRot = t.rotation;

        float dur = Mathf.Max(0.05f, dropDuration);
        float el = 0f;
        while (el < dur)
        {
            el += Time.deltaTime;
            float k = Mathf.Clamp01(el / dur);
            float eased = 1f - (1f - k) * (1f - k);
            SetTilt(t, hinge, axis, startPos, startRot, dropAngle * eased);
            yield return null;
        }

        // Phát âm thanh + bụi ngay lúc pallet chạm đất.
        if (dropDust) dropDust.Play();
        if (dropSound) AudioSource.PlayClipAtPoint(dropSound, transform.position);
        else SoundManager.I?.PlaySFX(SoundManager.I.palletDrop, transform.position);

        // Nảy nhẹ khi chạm đất.
        const float bounceTime = 0.08f;
        for (float b = 0f; b < bounceTime; b += Time.deltaTime)
        {
            float k = b / bounceTime;
            SetTilt(t, hinge, axis, startPos, startRot, dropAngle - dropBounce * Mathf.Sin(Mathf.PI * k));
            yield return null;
        }

        // Đoán trước mà server chưa xác nhận: giữ mô hình nằm tới hạn chờ; quá hạn vẫn chưa Dropped thì bị từ chối -> dựng lại.
        if (predicting && CurrentState == PalletState.Standing)
        {
            standingVisual.SetActive(false);
            if (droppedVisual) droppedVisual.SetActive(true);
            while (CurrentState == PalletState.Standing && Time.time < predictUntil) yield return null;
        }
        predicting = false;

        RestoreVisualPose(standingVisual);
        dropRoutine = null;
        ApplyCollider(CurrentState);
        ApplyVisuals(CurrentState);
    }

    static void SetTilt(Transform t, Vector3 hinge, Vector3 axis, Vector3 startPos, Quaternion startRot, float angle)
    {
        Quaternion q = Quaternion.AngleAxis(angle, axis);
        t.position = hinge + q * (startPos - hinge);
        t.rotation = q * startRot;
    }

    /// <summary>Pallet bị phá: rung mạnh dần, tới ~70% thời gian thì vỡ thành mảnh văng ra, phần còn lại chờ server chốt trạng thái.</summary>
    IEnumerator BreakRoutine()
    {
        GameObject vis = droppedVisual != null ? droppedVisual : standingVisual;
        if (vis == null) { breakRoutine = null; yield break; }

        Transform t = vis.transform;
        SavePose(t);
        float dur = Mathf.Max(0.3f, breakAnimDuration);
        float burstAt = dur * 0.7f;

        float el = 0f;
        for (; el < burstAt; el += Time.deltaTime)
        {
            float k = el / burstAt;
            float amp = 0.01f + 0.05f * k;
            t.localPosition = savedPos + Random.insideUnitSphere * amp;
            t.localRotation = savedRot * Quaternion.Euler(Random.Range(-1f, 1f) * 4f * k, 0f, Random.Range(-1f, 1f) * 4f * k);
            yield return null;
        }

        if (predictingBreak && !IsBreaking.Value)
        {
            CancelPredictedBreak();   // server chưa xác nhận tới mốc vỡ: coi như bị từ chối
            yield break;
        }
        predictingBreak = false;

        t.localPosition = savedPos;
        t.localRotation = savedRot;
        SpawnDebris(vis);
        shattered = true;
        vis.SetActive(false);
        if (breakSound) AudioSource.PlayClipAtPoint(breakSound, transform.position);
        else SoundManager.I?.PlaySFX(SoundManager.I.palletBreak, transform.position);

        if (el < dur) yield return new WaitForSeconds(dur - el);

        breakRoutine = null;
        ApplyVisuals(CurrentState);   // Destroyed: ẩn hết; nếu server chưa kịp chốt thì vẫn giữ ẩn nhờ cờ shattered
    }

    /// <summary>Tạo các mảnh gỗ (khối nhỏ cùng chất liệu với pallet) văng ra rồi rơi, thu nhỏ và biến mất.</summary>
    void SpawnDebris(GameObject source)
    {
        Renderer r = source.GetComponentInChildren<Renderer>();
        if (r == null || debrisCount <= 0) return;

        Bounds b = r.bounds;
        Material mat = r.sharedMaterial;
        var pieces = new List<DebrisPiece>(debrisCount);

        for (int i = 0; i < debrisCount; i++)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Collider c = g.GetComponent<Collider>();
            if (c != null) Destroy(c);
            Renderer pr = g.GetComponent<Renderer>();
            if (mat != null) pr.sharedMaterial = mat;

            Vector3 pos = new Vector3(
                Random.Range(b.min.x, b.max.x), Random.Range(b.min.y, b.max.y), Random.Range(b.min.z, b.max.z));
            g.transform.position = pos;
            g.transform.rotation = Random.rotation;
            g.transform.localScale = new Vector3(Random.Range(0.06f, 0.2f), Random.Range(0.04f, 0.14f), Random.Range(0.1f, 0.35f));

            Vector3 outward = pos - b.center; outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f) outward = Random.insideUnitSphere;
            pieces.Add(new DebrisPiece
            {
                t = g.transform,
                vel = outward.normalized * Random.Range(1.2f, 3.2f) + Vector3.up * Random.Range(1.5f, 3.5f),
                spin = Random.insideUnitSphere * Random.Range(180f, 540f),
                baseScale = g.transform.localScale
            });
        }
        StartCoroutine(DebrisRoutine(pieces, transform.position.y));
    }

    class DebrisPiece { public Transform t; public Vector3 vel; public Vector3 spin; public Vector3 baseScale; }

    IEnumerator DebrisRoutine(List<DebrisPiece> pieces, float groundY)
    {
        float life = Mathf.Max(0.3f, debrisLifetime);
        for (float el = 0f; el < life; el += Time.deltaTime)
        {
            float dt = Time.deltaTime;
            float shrink = Mathf.Clamp01((life - el) / (life * 0.3f));   // 0,3 giây cuối thu nhỏ dần
            foreach (DebrisPiece d in pieces)
            {
                if (d.t == null) continue;
                d.vel += Vector3.down * 9.8f * dt;
                Vector3 pos = d.t.position + d.vel * dt;
                float floor = groundY + d.baseScale.y * 0.5f;
                if (pos.y < floor) { pos.y = floor; d.vel.y = -d.vel.y * 0.3f; d.vel.x *= 0.6f; d.vel.z *= 0.6f; d.spin *= 0.6f; }
                d.t.position = pos;
                d.t.Rotate(d.spin * dt, Space.Self);
                d.t.localScale = d.baseScale * shrink;
            }
            yield return null;
        }
        foreach (DebrisPiece d in pieces) if (d.t != null) Destroy(d.t.gameObject);
    }

    // ── Server: kiểm tra hợp lệ ───────────────────────────────────────────────

    /// <summary>
    /// Server kiểm tra người gửi yêu cầu: trận chưa kết thúc, đúng vai, đang đứng trong vùng pallet (cho dung sai),
    /// Survivor còn hoạt động (không gục / bị bế / lên ghế), Hunter đang rảnh tay (không đang đánh, không đang bế).
    /// Hỗ trợ bot: nếu clientId không còn trong ConnectedClients, tìm trong BotController.ActiveBots.
    /// </summary>
    bool ServerCheckActor(ulong clientId, Role expected, out GameObject player)
    {
        player = null;
        if (MatchManager.Ended) return false;
        if (RoleAssignments.Get(clientId) != expected) return false;

        // Thử từ ConnectedClients trước
        if (NetworkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) && client.PlayerObject != null)
        {
            player = client.PlayerObject.gameObject;
        }
        // Fallback: bot đang hoạt động
        else if (BotController.ActiveBots.TryGetValue(clientId, out BotController bot) && bot != null)
        {
            player = bot.gameObject;
        }

        if (player == null) return false;
        if (!TryGetSide(player.transform.position, out _, out _, ServerRangeMargin)) return false;

        if (expected == Role.Survivor)
        {
            SurvivorState st = player.GetComponent<SurvivorState>();
            return st != null && st.IsActive;
        }

        HunterAttack atk = player.GetComponent<HunterAttack>();
        if (atk != null && !atk.ServerCanAct) return false;
        HunterCarry carry = player.GetComponent<HunterCarry>();
        if (carry != null && carry.IsCarrying) return false;
        return true;
    }

    // ── RPC: Survivor đập pallet xuống ───────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void DropServerRpc(ServerRpcParams p = default)
    {
        ulong clientId = p.Receive.SenderClientId;
        ServerDrop(clientId);
    }

    /// <summary>Bot trên server gọi trực tiếp, dùng OriginalClientId.</summary>
    public void ServerDrop(ulong clientId)
    {
        if (!IsServer || !IsStanding) return;
        if (!ServerCheckActor(clientId, Role.Survivor, out GameObject survivorGo)) return;

        // Pallet đổ về phía ĐỐI DIỆN Survivor (về phía Hunter). Ghi trước State để mọi máy đọc kịp khi State đổi.
        if (TryGetSide(survivorGo.transform.position, out int dropperSide, out _, ServerRangeMargin))
            FallSign.Value = (sbyte)-dropperSide;
        State.Value = (byte)PalletState.Dropped;
        TriggerAnimClientRpc("Drop");
        PlayActorAnim(survivorGo, "pallet_drop");
        StunHuntersInDropZone();
        PushSurvivorOutOfPallet(clientId, survivorGo);
    }

    // ── RPC: Hunter phá pallet ───────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void BreakServerRpc(ServerRpcParams p = default)
    {
        ulong clientId = p.Receive.SenderClientId;
        if (ServerBreak(clientId)) return;

        // Bị từ chối: báo riêng người gửi để hoàn lại pallet đoán trước và mở khóa Hunter.
        BreakRejectedClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        });
    }

    [ClientRpc]
    void BreakRejectedClientRpc(ClientRpcParams rpcParams = default)
    {
        CancelPredictedBreak();
        PalletInteract.Local?.OnBreakRejected();
    }

    /// <summary>Bot trên server gọi trực tiếp, dùng OriginalClientId.</summary>
    public bool ServerBreak(ulong clientId)
    {
        if (!IsServer || !IsDropped || breakInProgress) return false;
        if (!ServerCheckActor(clientId, Role.Hunter, out GameObject hunter)) return false;

        HunterAttack atk = hunter.GetComponent<HunterAttack>();
        if (atk != null) atk.ServerBeginAction(breakAnimDuration);

        breakInProgress = true;
        breakTimer = 0f;
        IsBreaking.Value = true;
        TriggerAnimClientRpc("Break");
        PlayActorAnim(hunter, "break_pallet");
        return true;
    }

    void Update()
    {
        if (!IsServer || !breakInProgress) return;

        breakTimer += Time.deltaTime;
        if (breakTimer >= breakAnimDuration)
        {
            breakInProgress = false;
            IsBreaking.Value = false;
            State.Value = (byte)PalletState.Destroyed;
        }
    }

    // ── Choáng Hunter khi pallet rơi ─────────────────────────────────────────

    void StunHuntersInDropZone()
    {
        foreach (PlayerRoleInfo info in FindObjectsByType<PlayerRoleInfo>(FindObjectsSortMode.None))
        {
            if (info.CurrentRole != Role.Hunter) continue;

            Vector3 toHunter = info.transform.position - transform.position;
            toHunter.y = 0f;
            if (toHunter.magnitude > stunRadius) continue;

            // Đẩy Hunter ra đúng phía mà họ đang đứng.
            Vector3 pushDir = Vector3.Dot(toHunter, Forward) >= 0f ? Forward : -Forward;
            Vector3 safePos = transform.position + pushDir * (halfDepth + 0.5f);
            safePos.y = info.transform.position.y;

            HunterAttack atk = info.GetComponent<HunterAttack>();
            if (atk != null) { atk.ServerBeginAction(stunDuration); atk.ServerPlayStun(stunDuration); }

            // Di chuyển là Owner-authoritative nên việc đẩy và khóa phải chạy trên MÁY CỦA HUNTER, không phải trên server.
            StunHunterClientRpc(new NetworkObjectReference(info.NetworkObject), safePos, info.transform.eulerAngles.y, stunDuration, new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { info.OwnerClientId } }
            });
            Debug.Log($"[Pallet] Hunter {info.OwnerClientId} bị choáng {stunDuration}s");
        }
    }

    /// <summary>
    /// Đẩy Survivor (người vừa đập pallet) ra đúng phía để tránh bị kẹt bên trong pallet khi nó rơi.
    /// Dựa vào dot product vị trí Survivor với trục Z (forward) của pallet, giống cách xử lý Hunter.
    /// </summary>
    void PushSurvivorOutOfPallet(ulong survivorClientId, GameObject survivorGo)
    {
        if (survivorGo == null) return;

        Vector3 toSurvivor = survivorGo.transform.position - transform.position;
        toSurvivor.y = 0f;

        // Nếu Survivor ở quá xa thì không cần đẩy
        if (toSurvivor.magnitude > stunRadius) return;

        Vector3 pushDir = Vector3.Dot(toSurvivor, Forward) >= 0f ? Forward : -Forward;
        Vector3 safePos = transform.position + pushDir * (halfDepth + 0.5f);
        safePos.y = survivorGo.transform.position.y;

        // Gửi snap tới máy đang SỞ HỮU object này (bot: host; người chơi: client của họ).
        // Không dùng survivorClientId vì với bot đó là id gốc của client đã rớt, không còn kết nối.
        NetworkObject survivorNo = survivorGo.GetComponent<NetworkObject>();
        if (survivorNo == null) return;
        PushSurvivorClientRpc(new NetworkObjectReference(survivorNo), safePos, survivorGo.transform.eulerAngles.y, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { survivorNo.OwnerClientId } }
        });
        Debug.Log($"[Pallet] Survivor {survivorClientId} được đẩy ra phía ({pushDir}) khi pallet rơi");
    }

    // ── ClientRpc ─────────────────────────────────────────────────────────────

    [ClientRpc]
    void TriggerAnimClientRpc(string triggerName)
    {
        if (animator != null && !proceduralAnimation) animator.SetTrigger(triggerName);   // đã dùng animation tự động thì khỏi kích Animator
    }

    // Báo mọi máy cho NHÂN VẬT (không phải pallet) chạy một trigger animation.
    void PlayActorAnim(GameObject actor, string triggerName)
    {
        NetworkObject no = actor != null ? actor.GetComponent<NetworkObject>() : null;
        if (no != null) ActorAnimClientRpc(new NetworkObjectReference(no), triggerName);
    }

    [ClientRpc]
    void ActorAnimClientRpc(NetworkObjectReference actor, string triggerName)
    {
        if (!actor.TryGet(out NetworkObject no)) return;
        if ((triggerName == "pallet_drop" || triggerName == "break_pallet") && no.IsOwner) return;   // Máy người đập/phá đã chạy local ngay lúc bấm phím

        Animator a = no.GetComponentInChildren<Animator>();
        if (a == null) return;
        int hash = Animator.StringToHash(triggerName);
        foreach (AnimatorControllerParameter p in a.parameters)
            if (p.nameHash == hash) { a.SetTrigger(hash); return; }   // thiếu tham số thì bỏ qua, không báo lỗi
    }

    /// <summary>Chỉ gửi tới máy của Hunter bị choáng: đẩy ra khỏi pallet và khóa di chuyển.</summary>
    [ClientRpc]
    void StunHunterClientRpc(NetworkObjectReference target, Vector3 pos, float yaw, float duration, ClientRpcParams rpcParams = default)
    {
        SoundManager.I?.PlaySFX(SoundManager.I.palletStun, transform.position);
        // Chỉ máy đang sở hữu đúng nhân vật Hunter này mới xử lý (không dùng LocalClient.PlayerObject:
        // khi Hunter là bot do host sở hữu, LocalClient.PlayerObject lại là Survivor của host).
        if (!target.TryGet(out NetworkObject no) || !no.IsOwner) return;

        NetworkPlayerMovement move = no.GetComponent<NetworkPlayerMovement>();
        if (move == null) return;

        move.SnapTo(pos, yaw);
        move.LockFor(duration);
    }

    /// <summary>Chỉ gửi tới máy của Survivor vừa đập pallet: đẩy ra đúng phía để tránh kẹt.</summary>
    [ClientRpc]
    void PushSurvivorClientRpc(NetworkObjectReference target, Vector3 pos, float yaw, ClientRpcParams rpcParams = default)
    {
        if (!target.TryGet(out NetworkObject no) || !no.IsOwner) return;

        NetworkPlayerMovement move = no.GetComponent<NetworkPlayerMovement>();
        if (move == null) return;

        move.SnapTo(pos, yaw);   // đẩy ra, không khóa di chuyển
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        Vector3 f = Forward;
        Quaternion rot = Quaternion.LookRotation(f, Vector3.up);

        // Vùng choáng Hunter (đỏ nhạt)
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.2f);
        Gizmos.DrawSphere(transform.position, stunRadius);

        // Vùng đứng để đập / trèo / phá (hộp vàng, giống VaultWindow)
        Gizmos.color = Color.yellow;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, rot, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(width, 0.1f, approachDepth * 2f));
        Gizmos.matrix = Matrix4x4.identity;

        // Điểm đáp (xanh lá)
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(transform.position + f * vaultLanding, 0.12f);
        Gizmos.DrawSphere(transform.position - f * vaultLanding, 0.12f);
        Gizmos.DrawLine(transform.position - f * vaultLanding, transform.position + f * vaultLanding);

        // Nửa chiều sâu pallet (xanh dương)
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, f * halfDepth);
        Gizmos.DrawRay(transform.position, -f * halfDepth);
    }
}