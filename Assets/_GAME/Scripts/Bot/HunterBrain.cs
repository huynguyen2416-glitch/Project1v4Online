using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Cây quyết định của Hunter bot. Kiểm tra theo thứ tự:
///   1. Đang bế người      -> mang tới ghế tên lửa trống gần nhất và đặt lên (phím bế).
///   2. Pallet đã đổ trước mặt -> phá (đang đuổi mà pallet chắn giữa hai người thì tự tới đứng trước pallet để phá).
///   3. Người gục trong tầm bế -> bế.
///   4. Có người gục gần    -> đi tới người đó.
///   5. Thấy Survivor       -> đuổi; vào tầm thì đánh; gặp cửa sổ khi đuổi thì nhảy qua.
///   6. Vừa mất dấu         -> tới chỗ cuối cùng thấy.
///   7. Còn lại             -> tuần tra giữa các máy chưa giải (hoặc các cổng khi đã đủ điện).
/// </summary>
public class HunterBrain : BotBrain
{
    const float SightRange = 25f;
    const float AttackRange = 1.7f;       // tâm tới tâm; hitbox thật: reach 1.5 + bán kính thân 0.35
    const float DownedNearRange = 6f;     // người gục trong tầm này thì tới bế dù không thấy
    const float LostMemory = 4f;          // đi tới chỗ cuối cùng thấy Survivor trong ngần này giây
    const float VaultCooldown = 3f;       // tránh nhảy qua nhảy lại cùng một cửa sổ

    Vector3 lastSeenPos;
    float lastSeenTime = -99f;
    float nextVaultAllowed;
    float blockWait;
    Object patrolTarget;
    Object currentTarget;
    Vector3 wanderPoint;
    float wanderUntil;

    public HunterBrain(BotController bot) : base(bot) { }

    public override void Think()
    {
        if (MatchManager.Ended) { Idle("Trận kết thúc"); return; }
        if (bot.Nav.ConsumeStuck() && currentTarget != null) Ignore(currentTarget, 10f);

        HunterCarry hc = bot.Carry;

        if (hc != null && hc.IsCarrying) { ThinkCarrying(hc); return; }

        // Pallet đã đổ trước mặt: phá.
        Pallet pal = bot.Pallet != null ? bot.Pallet.Target : null;
        if (pal != null && pal.IsDropped && !pal.IsBreaking.Value)
        {
            bot.Pad.PressPallet();
            Idle("Phá pallet");
            return;
        }

        // Người gục trong tầm bế (HunterCarry tự kiểm tra tầm và tường).
        if (hc != null && hc.PromptTarget != null)
        {
            bot.Pad.PressPickup();
            Idle("Bế người gục");
            return;
        }

        SurvivorState downed = NearestSurvivor(true);
        if (downed != null)
        {
            currentTarget = downed;
            SetGoal("Tới bế người gục");
            bot.Nav.SetDestination(downed.transform.position, 1.0f);
            return;
        }

        SurvivorState target = NearestSurvivor(false);
        if (target != null)
        {
            lastSeenPos = target.transform.position;
            lastSeenTime = Time.time;
            Chase(target);
            return;
        }

        if (Time.time - lastSeenTime < LostMemory)
        {
            SetGoal("Tìm chỗ cuối cùng thấy Survivor");
            bot.Nav.SetDestination(lastSeenPos, 1.5f);
            return;
        }

        Patrol();
    }

    // ---------- Bế ----------

    void ThinkCarrying(HunterCarry hc)
    {
        RocketChair chair = NearestFreeChair();
        if (chair == null)
        {
            bot.Pad.PressDrop();
            Idle("Không có ghế trống, thả người");
            return;
        }

        if (hc.PromptTarget != null)   // đang bế và đứng trong tầm ghế
        {
            bot.Pad.PressPickup();
            Idle("Đặt lên ghế tên lửa");
            return;
        }

        currentTarget = chair;
        SetGoal("Mang người tới ghế");
        bot.Nav.SetDestination(chair.transform.position, 1.8f);
    }

    RocketChair NearestFreeChair()
    {
        RocketChair best = null;
        float bestD = float.MaxValue;
        foreach (RocketChair c in RocketChair.All)
        {
            if (c == null || !c.IsSpawned || !c.IsFree || IsIgnored(c)) continue;
            float d = FlatTo(c.transform.position);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    // ---------- Đuổi và đánh ----------

    void Chase(SurvivorState target)
    {
        currentTarget = null;   // kẹt thì để BotNavigator tự gỡ; đừng bỏ qua Survivor đang đuổi
        float d = FlatTo(target.transform.position);

        if (d <= AttackRange)
        {
            bot.Pad.PressAttack();   // HunterAttack tự giữ hồi chiêu và khóa sau đòn
            SetGoal("Đánh Survivor");
            bot.Nav.SetDestination(target.transform.position, 1.0f);
            return;
        }

        // Pallet đã đổ nằm giữa Hunter và Survivor: đi tới điểm đứng trước pallet (trong vùng tương tác) để Think() phá nó.
        Pallet block = FindBlockingPallet(target.transform.position);
        if (block != null)
        {
            currentTarget = null;
            if (bot.Nav.Arrived) blockWait += bot.ThinkInterval; else blockWait = 0f;
            if (blockWait > 1.5f) { Ignore(block.transform, 6f); blockWait = 0f; }   // tới nơi mà vẫn không phá được thì bỏ, đi đường khác
            else
            {
                SetGoal("Tới phá pallet");
                bot.Nav.SetDestination(ApproachPoint(block.transform, Me.position), 0.3f);
                return;
            }
        }

        // Gặp cửa sổ trên đường đuổi thì nhảy qua (có hồi chiêu để khỏi nhảy qua nhảy lại).
        // Chỉ nhảy khi cửa sổ nằm GIỮA Hunter và Survivor (hai bên ở hai phía khác nhau). Bản cũ nhảy mọi cửa sổ đang đứng cạnh,
        // kể cả khi Survivor ở cùng phía hoặc cửa sổ nằm sau lưng, nên Hunter bot hay nhảy vô nghĩa rồi mất dấu.
        if (bot.Vault != null && bot.Vault.Target != null && Time.time >= nextVaultAllowed && d > 2.5f
            && !SameSide(bot.Vault.Target.transform, Me.position, target.transform.position))
        {
            nextVaultAllowed = Time.time + VaultCooldown;
            bot.Pad.PressVault();
            SetGoal("Nhảy cửa sổ đuổi theo");
            return;
        }

        SetGoal("Đuổi Survivor");
        bot.Nav.SetDestination(target.transform.position, 1.0f);
    }

    /// <summary>Pallet đã đổ trong 4 m mà Hunter và Survivor đang ở hai phía khác nhau (tức là nó chắn đường đuổi).</summary>
    Pallet FindBlockingPallet(Vector3 targetPos)
    {
        foreach (Pallet p in Pallet.All)
        {
            if (p == null || !p.IsDropped || p.IsBreaking.Value || IsIgnored(p.transform)) continue;
            if (FlatTo(p.transform.position) > 4f) continue;
            int sh = SideOf(p.transform, Me.position), st = SideOf(p.transform, targetPos);
            if (sh != 0 && st != 0 && sh != st) return p;
        }
        return null;
    }

    /// <summary>Survivor gần nhất. downed = true: người đang Gục; false: người còn hoạt động và nhìn thấy được.</summary>
    SurvivorState NearestSurvivor(bool downed)
    {
        SurvivorState best = null;
        float bestD = float.MaxValue;
        foreach (SurvivorState s in SurvivorState.All)
        {
            if (s == null || s.gameObject == bot.gameObject || IsIgnored(s)) continue;
            Vector3 p = s.transform.position;
            float d = FlatTo(p);

            if (downed)
            {
                if (s.Current != SurvivorHealth.Downed) continue;
                if (d > DownedNearRange && !Sees(p, SightRange)) continue;
            }
            else
            {
                if (!s.IsActive || !Sees(p, SightRange)) continue;
            }

            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    // ---------- Tuần tra ----------

    void Patrol()
    {
        if (patrolTarget != null && IsIgnored(patrolTarget)) patrolTarget = null;
        if (patrolTarget != null && bot.Nav.Arrived)
        {
            Ignore(patrolTarget, 25f);   // vừa kiểm tra xong chỗ này, lát nữa mới quay lại
            patrolTarget = null;
        }
        if (patrolTarget == null) patrolTarget = PickPatrolPoint();

        Transform t = (patrolTarget as Component) != null ? ((Component)patrolTarget).transform : null;
        if (t != null)
        {
            currentTarget = patrolTarget;
            SetGoal("Tuần tra " + t.name);
            bot.Nav.SetDestination(t.position, 3f);
            return;
        }

        WanderRandom();
    }

    Object PickPatrolPoint()
    {
        bool gatesPowered = MatchManager.Instance != null && MatchManager.Instance.GatesPowered.Value;
        Object chosen = null;
        int seen = 0;

        foreach (NetInteractable it in NetInteractable.All)
        {
            if (it == null || !it.IsSpawned || IsIgnored(it)) continue;
            bool ok = (it is CipherMachine && !it.Done.Value) || (gatesPowered && it is ExitGate && !it.Done.Value);
            if (!ok) continue;

            seen++;
            if (Random.Range(0, seen) == 0) chosen = it;   // chọn ngẫu nhiên đều trong các điểm hợp lệ
        }
        return chosen;
    }

    void WanderRandom()
    {
        currentTarget = null;
        if (Time.time > wanderUntil || bot.Nav.Arrived)
        {
            wanderUntil = Time.time + 8f;
            wanderPoint = Me.position;
            for (int i = 0; i < 5; i++)
            {
                Vector3 p = Me.position + Random.insideUnitSphere * 25f;
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 5f, NavMesh.AllAreas)) { wanderPoint = hit.position; break; }
            }
        }
        SetGoal("Tuần tra ngẫu nhiên");
        bot.Nav.SetDestination(wanderPoint, 1.5f);
    }
}