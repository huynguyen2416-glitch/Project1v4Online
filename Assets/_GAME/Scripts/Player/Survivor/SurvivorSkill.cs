using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Hệ thống kỹ năng Survivor. Gắn vào prefab Survivor.
/// Server quyết định lớp từ CharacterAssignments; mỗi máy đọc NetworkVariable để biết lớp + cooldown.
/// Owner bấm Q → gửi RPC lên server → server kiểm tra cooldown → kích hoạt → đồng bộ trạng thái.
///
/// 4 lớp:
///  0 Knight  – hóa vàng, chịu 1 đòn từ Hunter, CD 45s.
///  1 Athlete – tăng tốc 70% tức thì giảm dần trong 2s, CD 45s.
///  2 Doctor  – tự hồi máu (Injured→Healthy) không cần gục, không cooldown.
///  3 Sorcerer – tàng hình 2s (vẫn lộ dấu chân), CD 20s.
/// </summary>
[RequireComponent(typeof(SurvivorState))]
public class SurvivorSkill : NetworkBehaviour
{
    public static SurvivorSkill Local { get; private set; }

    [Header("Phím kích hoạt kỹ năng")]
    [SerializeField] KeyCode skillKey = KeyCode.Q;

    [Header("Knight – Tráng sĩ")]
    [SerializeField] float knightCooldown = 45f;
    [SerializeField] Color knightShieldColor = new Color(1f, 0.9f, 0.1f);   // vàng

    [Header("Athlete – VĐV")]
    [SerializeField] float athleteCooldown = 45f;
    [SerializeField] float athleteBoostPercent = 0.70f;   // 70%
    [SerializeField] float athleteBoostDuration = 2f;

    [Header("Doctor – Bác sĩ")]
    [SerializeField] float doctorHealDuration = 8f;   // thời gian giữ phím để tự lành (giây)

    [Header("Sorcerer – Ma thuật sư")]
    [SerializeField] float sorcererCooldown = 20f;
    [SerializeField] float sorcererDuration = 2f;

    // ─── Đồng bộ ─────────────────────────────────────────
    /// <summary>Lớp nhân vật. Server ghi lúc spawn.</summary>
    public readonly NetworkVariable<byte> ClassId = new NetworkVariable<byte>(0);

    /// <summary>Thời điểm server mà kỹ năng sẵn sàng (0 = sẵn sàng ngay). Mỗi máy đọc để hiện cooldown.</summary>
    public readonly NetworkVariable<float> CooldownReadyAt = new NetworkVariable<float>(0f);

    /// <summary>Kỹ năng đang hoạt động (Knight shield / Sorcerer invis). Mỗi máy đọc để áp hiệu ứng.</summary>
    public readonly NetworkVariable<bool> SkillActive = new NetworkVariable<bool>(false);

    // ─── Nội bộ ──────────────────────────────────────────
    SurvivorState state;
    NetworkPlayerMovement move;
    Renderer[] renderers;
    MaterialPropertyBlock block;
    CharacterInput input;

    // Server – Knight
    bool knightShieldUp;

    // Server – Sorcerer
    float sorcererEndTime;

    // Server – Doctor tự lành
    float doctorHealProgress;
    bool doctorHealing;      // owner đang giữ phím

    // Owner – Doctor
    bool ownerDoctorHolding;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId     = Shader.PropertyToID("_Color");

    public SurvivorClass Class => (SurvivorClass)ClassId.Value;

    /// <summary>Cooldown còn lại (giây). Dùng cho UI.</summary>
    public float CooldownRemaining
    {
        get
        {
            if (Class == SurvivorClass.Doctor) return 0f;   // Doctor không có cooldown
            float left = CooldownReadyAt.Value - NetworkTime;
            return left > 0f ? left : 0f;
        }
    }

    /// <summary>Tổng cooldown (giây) của lớp hiện tại.</summary>
    public float CooldownTotal
    {
        get
        {
            switch (Class)
            {
                case SurvivorClass.Knight:   return knightCooldown;
                case SurvivorClass.Athlete:  return athleteCooldown;
                case SurvivorClass.Sorcerer: return sorcererCooldown;
                default:                     return 0f;
            }
        }
    }

    /// <summary>Kỹ năng sẵn sàng chưa.</summary>
    public bool IsReady => CooldownRemaining <= 0f;

    /// <summary>Tên kỹ năng.</summary>
    public string SkillName
    {
        get
        {
            switch (Class)
            {
                case SurvivorClass.Knight:   return "Tráng sĩ";
                case SurvivorClass.Athlete:  return "Tăng tốc";
                case SurvivorClass.Doctor:   return "Tự lành";
                case SurvivorClass.Sorcerer: return "Tàng hình";
                default:                     return "";
            }
        }
    }

    /// <summary>Mô tả ngắn.</summary>
    public string SkillDescription
    {
        get
        {
            switch (Class)
            {
                case SurvivorClass.Knight:   return "Chịu 1 đòn";
                case SurvivorClass.Athlete:  return "+70% tốc độ";
                case SurvivorClass.Doctor:   return "Tự hồi máu";
                case SurvivorClass.Sorcerer: return "Tàng hình 2s";
                default:                     return "";
            }
        }
    }

    float NetworkTime => (float)(NetworkManager.Singleton != null
        ? NetworkManager.Singleton.ServerTime.Time : Time.timeAsDouble);

    // ═══════════════════════ Unity ═══════════════════════

    void Awake()
    {
        state     = GetComponent<SurvivorState>();
        move      = GetComponent<NetworkPlayerMovement>();
        renderers = GetComponentsInChildren<Renderer>(true);
        block     = new MaterialPropertyBlock();
        input     = CharacterInput.Of(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        if (IsLocalPlayer) Local = this;

        // Server: gán lớp dựa vào CharacterAssignments (chỉ số nhân vật = lớp)
        if (IsServer)
        {
            PlayerRoleInfo info = GetComponent<PlayerRoleInfo>();
            if (info != null && info.CurrentRole == Role.Survivor)
            {
                BotController bc = GetComponent<BotController>();
                ulong effectiveId = (bc != null && bc.OriginalClientId != 0) ? bc.OriginalClientId : OwnerClientId;
                int ci = CharacterAssignments.Get(effectiveId);
                ClassId.Value = (byte)ci;
            }
        }

        ClassId.OnValueChanged   += OnClassChanged;
        SkillActive.OnValueChanged += OnActiveChanged;
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
        ClassId.OnValueChanged     -= OnClassChanged;
        SkillActive.OnValueChanged -= OnActiveChanged;
    }

    void OnClassChanged(byte old, byte nw) { /* UI tự cập nhật từ ClassId */ }

    void OnActiveChanged(bool old, bool nw)
    {
        // Áp hiệu ứng trên mọi máy
        if (Class == SurvivorClass.Knight)
        {
            ApplyKnightVisual(nw);
            if (nw) SoundManager.I?.PlaySFX(SoundManager.I.skillKnight, transform.position);
            else if (old && !nw) SoundManager.I?.PlaySFX(SoundManager.I.skillKnightAbsorb, transform.position);
        }
        else if (Class == SurvivorClass.Sorcerer)
        {
            ApplySorcererVisual(nw);
            if (nw) SoundManager.I?.PlaySFX(SoundManager.I.skillSorcerer, transform.position);
        }
    }

    void Update()
    {
        if (!IsSpawned) return;

        if (IsOwner) OwnerUpdate();
        if (IsServer) ServerUpdate();
    }

    // ═══════════════════════ Owner ═══════════════════════

    void OwnerUpdate()
    {
        if (MatchManager.Ended) return;
        if (state == null || state.IsOut) return;

        // Doctor: giữ phím Q liên tục để tự lành (Injured → Healthy, không cần gục)
        if (Class == SurvivorClass.Doctor)
        {
            bool wantHold = state.Current == SurvivorHealth.Injured && Input.GetKey(skillKey);
            if (wantHold != ownerDoctorHolding)
            {
                ownerDoctorHolding = wantHold;
                DoctorHoldServerRpc(wantHold);
                if (move != null) move.SetLocked(this, wantHold);
            }
            return;
        }

        // Các lớp khác: bấm Q 1 lần
        if (Input.GetKeyDown(skillKey))
            UseSkillServerRpc();
    }

    // ═══════════════════════ Server ═══════════════════════

    void ServerUpdate()
    {
        // Sorcerer: hết thời gian tàng hình
        if (Class == SurvivorClass.Sorcerer && SkillActive.Value && Time.time >= sorcererEndTime)
        {
            SkillActive.Value = false;
        }

        // Knight: mất shield khi trạng thái đổi (đã chịu đòn rồi server xử lý ở ServerHitWithShield)
        if (Class == SurvivorClass.Knight && knightShieldUp && !SkillActive.Value)
        {
            knightShieldUp = false;
        }

        // Doctor: server cộng tiến độ
        if (Class == SurvivorClass.Doctor && doctorHealing)
        {
            if (state.Current != SurvivorHealth.Injured || MatchManager.Ended)
            {
                doctorHealing = false;
                doctorHealProgress = 0f;
                return;
            }

            doctorHealProgress += Time.deltaTime / Mathf.Max(0.1f, doctorHealDuration);
            if (doctorHealProgress >= 1f)
            {
                doctorHealProgress = 0f;
                doctorHealing = false;
                state.ServerHeal();   // Injured → Healthy
                // Báo owner mở khóa di chuyển
                DoctorReleaseClientRpc(new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
                });
            }
        }
    }

    // ─── RPC ─────────────────────────────────────────────

    [ServerRpc]
    void UseSkillServerRpc()
    {
        if (state == null || !state.IsActive || MatchManager.Ended) return;

        float now = NetworkTime;
        if (CooldownReadyAt.Value > now) return;   // còn cooldown

        switch (Class)
        {
            case SurvivorClass.Knight:
                knightShieldUp = true;
                SkillActive.Value = true;
                CooldownReadyAt.Value = now + knightCooldown;
                break;

            case SurvivorClass.Athlete:
                // Tăng tốc 70% giảm dần trong 2s – gửi RPC cho owner
                AthleteBoostClientRpc(1f + athleteBoostPercent, athleteBoostDuration,
                    new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
                    });
                CooldownReadyAt.Value = now + athleteCooldown;
                break;

            case SurvivorClass.Sorcerer:
                sorcererEndTime = Time.time + sorcererDuration;
                SkillActive.Value = true;
                CooldownReadyAt.Value = now + sorcererCooldown;
                break;
        }
    }

    [ServerRpc]
    void DoctorHoldServerRpc(bool holding)
    {
        doctorHealing = holding && state.Current == SurvivorHealth.Injured;
        if (!doctorHealing) doctorHealProgress = 0f;
    }

    [ClientRpc]
    void AthleteBoostClientRpc(float multiplier, float duration, ClientRpcParams rpcParams = default)
    {
        if (IsOwner && move != null)
        {
            move.AddSpeedBoost(multiplier, duration);
            SoundManager.I?.PlaySFX(SoundManager.I.skillAthlete, transform.position);
        }
    }

    [ClientRpc]
    void DoctorReleaseClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        ownerDoctorHolding = false;
        if (move != null) move.SetLocked(this, false);
        SoundManager.I?.PlaySFX(SoundManager.I.skillDoctor, transform.position);
    }

    // ═══════════════════ Knight – Chịu đòn ═══════════════════

    /// <summary>
    /// Gọi từ SurvivorState.ServerHit() TRƯỚC khi trừ máu.
    /// Trả true nếu Knight đang có shield → hấp thụ đòn, không trừ máu.
    /// </summary>
    public bool TryAbsorbHit()
    {
        if (!IsServer) return false;
        if (Class != SurvivorClass.Knight || !knightShieldUp) return false;

        knightShieldUp = false;
        SkillActive.Value = false;   // tắt hiệu ứng vàng
        return true;   // đòn bị hấp thụ
    }

    // ═══════════════════ Sorcerer – Tàng hình ═══════════════════

    /// <summary>Mọi máy gọi: Sorcerer đang tàng hình?</summary>
    public bool IsInvisible => Class == SurvivorClass.Sorcerer && SkillActive.Value;

    // ═══════════════════ Doctor – Tự lành ═══════════════════

    /// <summary>Tiến độ tự lành (0..1). UI đọc.</summary>
    public float DoctorHealPercent => doctorHealing ? doctorHealProgress : 0f;

    /// <summary>Lời nhắc cho InteractUI khi Doctor đang tự lành.</summary>
    public string DoctorPrompt
    {
        get
        {
            if (!IsOwner || Class != SurvivorClass.Doctor) return null;
            if (state.Current != SurvivorHealth.Injured) return null;
            int pct = Mathf.RoundToInt(Mathf.Clamp01(DoctorHealPercent) * 100f);
            return ownerDoctorHolding
                ? $"Đang tự lành... {pct}%"
                : $"Giữ [{skillKey}] để tự lành";
        }
    }

    // ═══════════════════ Hiệu ứng ═══════════════════

    void ApplyKnightVisual(bool on)
    {
        Color c = on ? knightShieldColor : Color.white;
        // Nếu đang bị thương thì dùng lại màu injured (SurvivorState.Apply sẽ tô lại)
        if (!on && state != null) return;   // SurvivorState.Apply() sẽ áp đúng màu
        SetTint(c);
    }

    void ApplySorcererVisual(bool on)
    {
        // Tàng hình: mọi máy KHÁC ẩn model. Máy chủ nhân vẫn thấy mờ.
        foreach (Renderer r in renderers)
        {
            if (r == null) continue;
            if (on)
            {
                if (IsOwner)
                {
                    // Chủ nhân thấy mờ
                    r.GetPropertyBlock(block);
                    Color ghost = new Color(1f, 1f, 1f, 0.3f);
                    block.SetColor(BaseColorId, ghost);
                    block.SetColor(ColorId, ghost);
                    r.SetPropertyBlock(block);
                }
                else
                {
                    r.enabled = false;   // người khác không thấy
                }
            }
            else
            {
                r.enabled = true;
                // SurvivorState.Apply() sẽ tô đúng màu
            }
        }
    }

    void SetTint(Color c)
    {
        foreach (Renderer r in renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);
            block.SetColor(ColorId, c);
            r.SetPropertyBlock(block);
        }
    }
}
