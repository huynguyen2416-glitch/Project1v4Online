using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Cây quyết định của Survivor bot. Kiểm tra theo thứ tự, việc nào đúng thì làm và dừng:
///   1. Bị bế / ngồi ghế / đã thoát / bị loại  -> đứng yên.
///   2. Gục                                    -> bò tới cổng nếu đã mở, không thì chờ được cứu.
///   3. Biết có Hunter (nhìn thấy, hoặc trong "bán kính tim đập") hoặc vừa mất dấu -> chạy trốn:
///        - buông việc đang làm ngay (đang giải mã cũng bỏ);
///        - chạy tới ĐIỂM ĐỨNG TRƯỚC pallet / cửa sổ gần nhất mà tới trước Hunter;
///        - pallet: chờ Hunter vào tầm rồi đập, sau đó trèo sang phía bên kia MỘT lần;
///        - cửa sổ: nhảy khi Hunter cùng phía; không có gì để dùng thì chạy xa theo đường NavMesh.
///   4. Cổng đã mở                             -> đi tới cổng để thoát.
///   5. Hầm đã mở > mở cổng (đủ điện) > cứu / hồi máu đồng đội (Hunter xa) > giải máy gần nhất
///      (bỏ qua các việc nằm gần chỗ Hunter vừa xuất hiện).
///   6. Hết việc                               -> đi dạo, tránh xa chỗ Hunter vừa xuất hiện.
///
/// Các lỗi của bản cũ đã sửa ở đây:
///   - Pallet / cửa sổ: bot trèo qua rồi lại trèo ngược lại (vì vẫn đứng trong vùng sau khi đáp). Nay mỗi chướng ngại chỉ dùng
///     một lần rồi "bỏ qua" một lúc, và chỉ trèo khi Hunter đang ở CÙNG phía (trèo là để sang phía xa Hunter).
///   - Bot chạy tới tâm pallet nên hay đứng ngoài vùng tương tác (bị collider chặn bên hông). Nay chạy tới điểm trên trục xuyên qua pallet.
///   - Không phát hiện được Hunter khi đang giải mã (tia nhìn bị máy / tường chắn). Nay có thêm bán kính tim đập không cần nhìn thấy.
///   - Đang giải mã gặp Hunter chỉ dựa vào việc di chuyển để buông; nay bấm F buông thẳng cho chắc.
///   - Bấm F mà server từ chối thì bot đứng "đang làm" mãi. Nay thấy tiến độ không đổi 4 giây thì buông và đổi việc.
///   - Bot quay lại đúng cái máy cạnh Hunter ngay khi hết "trí nhớ". Nay tránh các việc gần chỗ Hunter vừa xuất hiện trong 20 giây.
/// </summary>
public class SurvivorBrain : BotBrain
{
    const float SightRange = 20f;          // thấy Hunter (không bị tường chắn) trong tầm này thì chạy
    const float TerrorRadius = 10f;        // Hunter gần hơn mức này thì biết luôn dù không nhìn thấy
    const float ThreatMemory = 4f;         // vẫn coi là có Hunter thêm ngần này giây sau khi mất dấu
    const float DangerMemory = 20f;        // nhớ chỗ Hunter lần cuối để tránh các việc ở gần đó
    const float AvoidTaskRadius = 14f;     // việc nằm trong bán kính này quanh chỗ Hunter vừa xuất hiện thì bỏ qua
    const float DropRange = 2.0f;          // Hunter cách TÂM PALLET gần hơn mức này mới đập (vùng choáng của pallet là 2,2 m)
    const float VaultPalletRange = 10f;    // pallet đã nằm: trèo qua khi Hunter cùng phía và còn trong tầm này (để pallet chắn giữa hai bên)
    const float ShortcutRange = 7f;        // tìm pallet đã nằm để trèo đi tắt trong bán kính này
    const float DropPanicRange = 3.0f;     // Hunter cách BOT gần hơn mức này thì đập luôn dù chưa vào vùng choáng
    const float CloseThreat = 6f;          // Survivor KHÔNG bị truy đuổi chỉ bỏ việc chạy khi Hunter gần hơn mức này
    const float ChaseMargin = 1.5f;        // Mình chỉ là "người bị đuổi" khi không có ai gần Hunter hơn mình trừ ngần này mét
    const float VaultWindowRange = 8f;     // Hunter gần hơn mức này mới nhảy cửa sổ
    const float MaxWaitAtObstacle = 10f;   // chờ Hunter lại gần tối đa ngần này giây rồi thôi
    const float HelpSafeRange = 20f;       // chỉ đi cứu / hồi máu khi Hunter (nếu có) xa hơn mức này
    const float ObstacleSearch = 18f;      // tìm pallet / cửa sổ để chạy tới trong bán kính này
    const float StallSeconds = 4f;         // đang "làm" mà tiến độ đứng yên ngần này giây thì buông

    Vector3 lastHunterPos;
    float lastHunterTime = -99f;     // lần cuối thấy Hunter (dùng để tránh các việc ở gần chỗ đó)
    float lastThreatTime = -99f;     // lần cuối mình là người bị Hunter nhắm tới (dùng để quyết định chạy trốn)

    static SurvivorState[] survivors = new SurvivorState[0];
    static float survivorsAt = -99f;

    Object currentTarget;
    float waitInRange;
    float lastProgress = -1f;
    float progressChangedAt;

    Vector3 wanderPoint;
    float wanderUntil;

    Transform fleeObstacle;     // giữ mục tiêu chạy trốn một lúc, tránh đổi ý mỗi 0,25 giây
    float fleeCommitUntil;
    Vector3 fleePoint;
    float fleePointUntil;

    Object waitingFor;          // pallet / cửa sổ đang đứng chờ Hunter lại gần
    float waitUntil;

    NavMeshPath probePath;

    public SurvivorBrain(BotController bot) : base(bot) { }

    public override void Think()
    {
        SurvivorState me = bot.Survivor;
        if (me == null || MatchManager.Ended) { Idle("Không hoạt động"); return; }

        if (bot.Nav.ConsumeStuck())
        {
            if (currentTarget != null) Ignore(currentTarget, 12f);   // kẹt thì bỏ mục tiêu này một lúc
            if (fleeObstacle != null) { Ignore(fleeObstacle, 8f); fleeObstacle = null; }
            fleePointUntil = 0f;
        }

        switch (me.Current)
        {
            case SurvivorHealth.Healthy:
            case SurvivorHealth.Injured:
                break;
            case SurvivorHealth.Downed:
                ThinkDowned();
                return;
            default:
                Idle(me.Current.ToString());   // Carried, OnChair, Escaped, Eliminated
                return;
        }

        Transform hunter = NearestHunter(out float hunterDist);
        bool seen = hunter != null && hunterDist <= SightRange && Sees(hunter.position, SightRange);
        bool heard = hunter != null && hunterDist <= TerrorRadius;   // không cần nhìn thấy
        bool aware = seen || heard;
        if (aware)
        {
            lastHunterPos = hunter.position;
            lastHunterTime = Time.time;
        }

        // Chỉ người bị truy đuổi (gần Hunter nhất) mới chạy trốn; những người còn lại tiếp tục giải máy / cứu người.
        // Ngoại lệ: Hunter đã áp sát thì ai cũng phải tránh.
        bool targeted = aware && (IsChaseTarget(hunter) || hunterDist < CloseThreat);
        if (targeted) lastThreatTime = Time.time;
        bool threatened = targeted || Time.time - lastThreatTime < ThreatMemory;

        Decide(threatened, hunter != null ? hunterDist : float.MaxValue);
    }

    /// <summary>
    /// Mình có phải Survivor gần Hunter nhất không (không tính người đã gục / bị bế / lên ghế / rời trận)?
    /// Có thêm ChaseMargin để khi hai người ngang nhau thì không đổi qua đổi lại.
    /// </summary>
    static void RefreshSurvivors()
    {
        if (Time.time - survivorsAt <= 0.5f) return;
        survivors = Object.FindObjectsByType<SurvivorState>(FindObjectsSortMode.None);
        survivorsAt = Time.time;
    }

    bool IsChaseTarget(Transform hunter)
    {
        RefreshSurvivors();

        float mine = Flat(Me.position, hunter.position);
        foreach (SurvivorState s in survivors)
        {
            if (s == null || s.gameObject == bot.gameObject || !s.IsActive) continue;
            if (Flat(s.transform.position, hunter.position) < mine - ChaseMargin) return false;
        }
        return true;
    }

    // ---------- Gục ----------

    void ThinkDowned()
    {
        ExitGate open = FindOpenGate();
        if (open != null)
        {
            SetGoal("Bò tới cổng đã mở");
            bot.Nav.SetDestination(open.ExitPosition, 0.5f);
            return;
        }
        Idle("Gục, chờ được cứu");
    }

    // ---------- Chạy trốn ----------

    void Flee(Vector3 hunterPos)
    {
        float hd = FlatTo(hunterPos);

        // Đang giải mã / mở cổng / hồi máu thì buông ngay (bấm F lần nữa = buông). Việc di chuyển bên dưới cũng làm buông.
        PlayerInteractor pi = bot.Interactor;
        if (pi != null && pi.IsWorking) bot.Pad.PressInteract();

        if (TryUseObstacle(hunterPos, hd)) return;

        Transform ob;
        if (fleeObstacle != null && Time.time < fleeCommitUntil && !IsIgnored(fleeObstacle) && ObstacleStillUsable(fleeObstacle))
        {
            ob = fleeObstacle;   // đang cam kết với mục tiêu này
        }
        else
        {
            ob = PickObstacle(hunterPos);
            fleeObstacle = ob;
            fleeCommitUntil = Time.time + 2f;
        }

        if (ob != null)
        {
            currentTarget = ob;
            SetGoal("Chạy tới " + ob.name);
            // Đi tới điểm trên trục xuyên qua pallet / cửa sổ (cùng phía với bot), KHÔNG đi tới tâm:
            // collider của pallet / cửa sổ chặn bot ở hai bên hông, tức là ngoài vùng tương tác.
            // Pallet đang đứng: đi tới phía XA Hunter (đi xuyên qua pallet đứng được) để đập khi Hunter tới sát giữa ván.
            // Pallet đã nằm / cửa sổ: đứng cùng phía với mình để trèo sang phía xa Hunter.
            Pallet standing = ob.GetComponent<Pallet>();
            Vector3 dest = (standing != null && standing.IsStanding)
                ? FarSidePoint(ob, hunterPos, Me.position)
                : ApproachPoint(ob, Me.position);
            bot.Nav.SetDestination(dest, 0.35f);
            return;
        }

        currentTarget = null;
        if (Time.time >= fleePointUntil || bot.Nav.Arrived)
        {
            fleePoint = PickFleePoint(hunterPos);
            fleePointUntil = Time.time + 1.5f;
        }
        SetGoal("Chạy trốn Hunter");
        bot.Nav.SetDestination(fleePoint, 1.5f);
    }

    /// <summary>
    /// Đang đứng trong vùng pallet / cửa sổ: quyết định bấm phím hay chờ.
    /// Mỗi chướng ngại chỉ dùng một lần rồi bị bỏ qua một lúc, để bot không trèo qua rồi trèo lại.
    /// </summary>
    bool TryUseObstacle(Vector3 hunterPos, float hd)
    {
        Pallet p = bot.Pallet != null ? bot.Pallet.Target : null;
        if (p != null && !p.IsDestroyed && !p.IsBreaking.Value && !IsIgnored(p.transform))
        {
            bool hunterSameSide = SameSide(p.transform, Me.position, hunterPos);

            if (p.IsStanding)
            {
                float hunterToPallet = Flat(hunterPos, p.transform.position);
                if (hunterToPallet <= DropRange || hd <= DropPanicRange)
                {
                    bot.Pad.PressPallet();   // đập xuống: Hunter trong vùng choáng bị choáng
                    SetGoal("Đập pallet");
                    return true;
                }
                // Đang đứng cùng phía với Hunter mà Hunter còn xa: chưa đứng đúng chỗ, tiếp tục đi sang phía bên kia ván.
                if (hunterSameSide) return false;
                if (WaitHere(p.transform, "Đứng sau pallet chờ Hunter tới sát để đập")) return true;
                return false;
            }

            // Pallet đã nằm. Trèo qua chỉ khi Hunter cùng phía (trèo sang phía xa Hunter); khác phía thì pallet đã chắn rồi.
            if (hunterSameSide && hd <= VaultPalletRange)
            {
                bot.Pad.PressPallet();
                Ignore(p.transform, 10f);   // đáp xuống vẫn nằm trong vùng pallet: không bấm lần nữa
                SetGoal("Trèo qua pallet");
                return true;
            }
            Ignore(p.transform, 6f);
            return false;
        }

        VaultWindow w = bot.Vault != null ? bot.Vault.Target : null;
        if (w != null && !IsIgnored(w.transform))
        {
            if (hd <= VaultWindowRange && SameSide(w.transform, Me.position, hunterPos))
            {
                bot.Pad.PressVault();
                Ignore(w.transform, 8f);
                SetGoal("Nhảy qua cửa sổ");
                return true;
            }
            if (hd > VaultWindowRange && WaitHere(w.transform, "Chờ Hunter lại gần để nhảy cửa sổ")) return true;
            Ignore(w.transform, 4f);
        }
        return false;
    }

    /// <summary>Đứng chờ tại chỗ tối đa MaxWaitAtObstacle giây; hết giờ thì bỏ qua chướng ngại này và trả về false.</summary>
    bool WaitHere(Transform t, string why)
    {
        if (waitingFor != t) { waitingFor = t; waitUntil = Time.time + MaxWaitAtObstacle; }
        if (Time.time < waitUntil) { Idle(why); return true; }
        waitingFor = null;
        Ignore(t, 10f);
        return false;
    }

    static bool ObstacleStillUsable(Transform t)
    {
        if (t == null) return false;
        Pallet p = t.GetComponent<Pallet>();
        return p == null || (!p.IsDestroyed && !p.IsBreaking.Value);
    }

    /// <summary>Pallet / cửa sổ gần nhất mà bot tới trước Hunter ít nhất 2 mét. Pallet còn đứng được ưu tiên hơn một chút.</summary>
    Transform PickObstacle(Vector3 hunterPos)
    {
        Transform best = null;
        float bestScore = float.MaxValue;

        foreach (Pallet p in Pallet.All)
        {
            if (p == null || p.IsDestroyed || p.IsBreaking.Value || IsIgnored(p.transform)) continue;
            Consider(p.transform, hunterPos, p.IsStanding ? -3f : 0f, ref best, ref bestScore);
        }
        foreach (VaultWindow w in VaultWindow.All)
        {
            if (w == null || IsIgnored(w.transform)) continue;
            Consider(w.transform, hunterPos, 0f, ref best, ref bestScore);
        }
        return best;
    }

    void Consider(Transform t, Vector3 hunterPos, float bias, ref Transform best, ref float bestScore)
    {
        float dm = FlatTo(t.position);
        float dh = Flat(hunterPos, t.position);
        if (dm > ObstacleSearch || dm + 2f >= dh) return;
        float score = dm + bias;
        if (score < bestScore) { best = t; bestScore = score; }
    }

    /// <summary>
    /// Lấy mẫu 12 hướng cách 12 mét trên NavMesh. Điểm = khoảng cách tới Hunter; loại điểm không có đường đi hoàn chỉnh
    /// hoặc đường vòng quá 30 m, và phạt điểm có đường đi lướt qua Hunter.
    /// </summary>
    Vector3 PickFleePoint(Vector3 hunterPos)
    {
        if (probePath == null) probePath = new NavMeshPath();
        bool haveFrom = NavMesh.SamplePosition(Me.position, out NavMeshHit from, 1.5f, NavMesh.AllAreas);

        Vector3 best = Vector3.zero;
        float bestScore = -1f;
        for (int i = 0; i < 12; i++)
        {
            float a = i * 30f * Mathf.Deg2Rad;
            Vector3 p = Me.position + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * 12f;
            if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 4f, NavMesh.AllAreas)) continue;
            if (Flat(hit.position, Me.position) < 5f) continue;

            float score = Flat(hit.position, hunterPos);
            if (haveFrom)
            {
                if (!NavMesh.CalculatePath(from.position, hit.position, NavMesh.AllAreas, probePath)
                    || probePath.status != NavMeshPathStatus.PathComplete) continue;
                if (PathLength(probePath) > 30f) continue;
                if (PathNear(probePath, hunterPos, 3f)) score -= 8f;
            }
            if (score > bestScore) { bestScore = score; best = hit.position; }
        }
        if (bestScore >= 0f) return best;

        // Không có NavMesh: chạy thẳng ra xa Hunter.
        Vector3 away = Me.position - hunterPos;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = Me.forward;
        return Me.position + away.normalized * 10f;
    }

    static float PathLength(NavMeshPath path)
    {
        Vector3[] c = path.corners;
        float len = 0f;
        for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
        return len;
    }

    static bool PathNear(NavMeshPath path, Vector3 point, float radius)
    {
        foreach (Vector3 c in path.corners)
            if (Flat(c, point) < radius) return true;
        return false;
    }

    // ---------- Làm nhiệm vụ ----------

    // ---------- Utility AI: chấm điểm từng hành động, chọn điểm cao nhất ----------

    /// <summary>
    /// Mỗi hành động có điểm 0..~1 (chạy trốn khi áp sát có thể vượt 1). Chọn hành động điểm cao nhất:
    ///   - Chạy trốn : 0,55 → 1 theo độ gần của Hunter (+0,1 khi đang bị thương); Hunter áp sát (< CloseThreat) thì luôn thắng.
    ///   - Ra cổng đã mở : 0,95.
    ///   - Việc cần làm : xem ScoreTask. Máy sắp xong có thể thắng cả chạy trốn khi Hunter còn xa.
    ///   - Đi dạo : khi không còn gì đáng làm.
    /// </summary>
    void Decide(bool threatened, float hunterDist)
    {
        ExitGate open = FindOpenGate();
        bool gateOpen = open != null && !IsIgnored(open);

        float fleeU = 0f;
        if (threatened)
        {
            float d = Mathf.Min(hunterDist, FlatTo(lastHunterPos));
            fleeU = 0.55f + 0.45f * (1f - Mathf.Clamp01(d / SightRange));
            if (bot.Survivor.Current == SurvivorHealth.Injured) fleeU += 0.1f;
            if (d < CloseThreat) fleeU = 1.5f;   // Hunter áp sát: luôn chạy
        }

        float gateU = gateOpen ? 0.95f : 0f;
        NetInteractable task = PickTask(hunterDist, out float taskU);

        if (fleeU > 0f && fleeU >= gateU && fleeU >= taskU)
        {
            Flee(lastHunterPos);
            return;
        }

        waitingFor = null;   // không còn đứng chờ ở pallet / cửa sổ

        if (gateU > 0f && gateU >= taskU)
        {
            currentTarget = open;
            if (TryShortcut(open.ExitPosition)) return;
            SetGoal("Ra cổng thoát");
            bot.Nav.SetDestination(open.ExitPosition, 0.5f);
            return;
        }

        if (task != null && taskU > 0.12f) { GoInteract(task); return; }
        Wander();
    }

    NetInteractable PickTask(float hunterDist, out float bestScore)
    {
        NetInteractable best = null;
        bestScore = 0f;
        bool danger = Time.time - lastHunterTime < DangerMemory;
        ulong checkId = bot.OriginalClientId != 0 ? bot.OriginalClientId : bot.OwnerClientId;
        RefreshSurvivors();

        foreach (NetInteractable it in NetInteractable.All)
        {
            if (it == null || !it.IsSpawned || IsIgnored(it)) continue;
            if (!it.IsAvailable || !it.CanUse(checkId)) continue;

            float sc = ScoreTask(it, checkId, hunterDist, danger);
            if (it == currentTarget) sc *= 1.2f;   // đang làm dở thì ưu tiên thêm, tránh đổi ý liên tục
            if (sc > bestScore) { bestScore = sc; best = it; }
        }
        return best;
    }

    float ScoreTask(NetInteractable it, ulong checkId, float hunterDist, bool danger)
    {
        float d = it.FlatDistance(Me.position);
        float s;

        if (it is Dungeon) s = 0.85f;
        else if (it is ExitGate) s = 0.8f;
        else if (it is RocketChair chair)
        {
            if (!bot.HelpTeammates) return 0f;
            s = 0.65f + 0.25f * (1f - Mathf.Clamp01(chair.TimeLeft.Value / 30f));   // sắp hết giờ thì gấp hơn
            ulong r = chair.RescuerId.Value;
            if (r != RocketChair.None && r != checkId) s *= 0.3f;                   // đã có người đang cứu
        }
        else if (it is HealInteractable)
        {
            if (!bot.HelpTeammates) return 0f;
            SurvivorState st = it.GetComponent<SurvivorState>();
            s = st != null && st.Current == SurvivorHealth.Downed ? 0.6f : 0.35f;   // đỡ người gục quan trọng hơn hồi máu
        }
        else
        {
            s = 0.5f;   // máy mã hóa
            int others = CountSurvivorsNear(it.transform.position, 4f);
            if (others >= 2) s *= 0.4f;          // đã đủ người, đi máy khác
            else if (others == 1) s *= 0.85f;
        }

        s *= 1f - 0.5f * Mathf.Clamp01(d / 50f);   // càng xa càng kém hấp dẫn

        if (!it.IsInstant)
        {
            float p = Mathf.Clamp01(it.Progress.Value);
            s += (it == currentTarget ? 0.5f : 0.15f) * p * p;   // sắp xong thì rất đáng làm nốt
        }

        if (danger)
        {
            float dh = Flat(it.transform.position, lastHunterPos);
            if (dh < AvoidTaskRadius) s *= 0.25f + 0.75f * (dh / AvoidTaskRadius);   // gần chỗ Hunter vừa xuất hiện: giảm điểm
        }
        if ((it is RocketChair || it is HealInteractable) && hunterDist < HelpSafeRange) s *= 0.3f;   // Hunter còn gần: đừng đi cứu

        return s;
    }

    /// <summary>Số Survivor khác (còn chơi) đứng trong bán kính radius quanh điểm p.</summary>
    int CountSurvivorsNear(Vector3 p, float radius)
    {
        int n = 0;
        foreach (SurvivorState st in survivors)
        {
            if (st == null || st.gameObject == bot.gameObject || !st.IsActive) continue;
            if (Flat(st.transform.position, p) <= radius) n++;
        }
        return n;
    }

    void GoInteract(NetInteractable it)
    {
        if (currentTarget != it) { currentTarget = it; waitInRange = 0f; lastProgress = -1f; }

        if (it.FlatDistance(Me.position) <= it.Range - 0.3f)
        {
            bot.Nav.Stop();   // đứng yên: di chuyển sẽ làm PlayerInteractor buông ra
            PlayerInteractor pi = bot.Interactor;

            if (pi != null && pi.IsWorking)
            {
                SetGoal(it.PromptText + " (đang làm)");
                waitInRange = 0f;

                // Server có thể từ chối lệnh bắt đầu mà PlayerInteractor vẫn báo "đang làm": thấy tiến độ không nhúc nhích thì buông.
                if (!it.IsInstant)
                {
                    float prog = it.Progress.Value;
                    if (lastProgress < 0f || Mathf.Abs(prog - lastProgress) > 0.0005f)
                    {
                        lastProgress = prog;
                        progressChangedAt = Time.time;
                    }
                    else if (Time.time - progressChangedAt > StallSeconds)
                    {
                        bot.Pad.PressInteract();   // bấm F lần nữa = buông
                        Ignore(it, 10f);
                        lastProgress = -1f;
                        SetGoal("Không có tiến độ, bỏ " + it.name);
                    }
                }
                return;
            }
            lastProgress = -1f;

            // Chỉ bấm khi thứ gần nhất mà PlayerInteractor thấy đúng là thứ bot định làm (kẻo bắt đầu nhầm việc khác).
            if (pi != null && pi.Target == it)
            {
                bot.Pad.PressInteract();
                SetGoal(it.PromptText);
            }

            waitInRange += bot.ThinkInterval;
            if (waitInRange > 3f) { Ignore(it, 15f); waitInRange = 0f; }   // đứng gần mà không dùng được thì thử thứ khác
            return;
        }

        if (TryShortcut(it.transform.position)) return;
        SetGoal("Đi tới " + it.name);
        bot.Nav.SetDestination(it.transform.position, Mathf.Max(0.5f, it.Range - 0.8f));
    }

    /// <summary>
    /// Đi tắt qua pallet đã nằm: nếu đích nằm bên kia pallet và đường NavMesh vòng quá xa (hoặc không có đường), thì đi tới
    /// sát pallet rồi bấm trèo. Trả về true nếu đang xử lý việc này (đã đặt đích hoặc đã bấm phím).
    /// </summary>
    bool TryShortcut(Vector3 goal)
    {
        Pallet pick = null;
        float bestD = ShortcutRange;
        foreach (Pallet p in Pallet.All)
        {
            if (p == null || !p.IsDropped || p.IsDestroyed || p.IsBreaking.Value || IsIgnored(p.transform)) continue;
            float d = FlatTo(p.transform.position);
            if (d > bestD) continue;

            int sb = SideOf(p.transform, Me.position), sg = SideOf(p.transform, goal);
            if (sb == 0 || sg == 0 || sb == sg) continue;                    // đích phải nằm bên kia pallet
            if (DistToSegment(p.transform.position, Me.position, goal) > 3f) continue;   // pallet phải nằm gần đường thẳng tới đích

            float straight = Flat(Me.position, goal);
            if (!NavDetourIsLong(goal, straight)) continue;                  // đường bộ không vòng nhiều thì khỏi trèo
            pick = p;
            bestD = d;
        }
        if (pick == null) return false;

        if (bot.Pallet != null && bot.Pallet.Target == pick)
        {
            bot.Pad.PressPallet();
            Ignore(pick.transform, 10f);   // đáp xuống vẫn nằm trong vùng pallet: không bấm lần nữa
            SetGoal("Trèo pallet đi tắt");
            return true;
        }
        SetGoal("Đi tới pallet để trèo tắt");
        bot.Nav.SetDestination(ApproachPoint(pick.transform, Me.position), 0.35f);
        return true;
    }

    bool NavDetourIsLong(Vector3 goal, float straight)
    {
        if (probePath == null) probePath = new NavMeshPath();
        if (!NavMesh.SamplePosition(Me.position, out NavMeshHit a, 2f, NavMesh.AllAreas)) return false;
        if (!NavMesh.SamplePosition(goal, out NavMeshHit b, 3f, NavMesh.AllAreas)) return false;
        if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, probePath)
            || probePath.status != NavMeshPathStatus.PathComplete) return true;   // không có đường bộ: chỉ còn cách trèo
        return PathLength(probePath) > straight * 1.4f + 2f;
    }

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        p.y = a.y = b.y = 0f;
        Vector3 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 0.0001f) return Vector3.Distance(p, a);
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
        return Vector3.Distance(p, a + ab * t);
    }

    void Wander()
    {
        currentTarget = null;
        if (Time.time > wanderUntil || bot.Nav.Arrived)
        {
            wanderUntil = Time.time + 8f;
            wanderPoint = Me.position;
            bool danger = Time.time - lastHunterTime < DangerMemory;
            float bestScore = -1f;
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = Me.position + Random.insideUnitSphere * 20f;
                if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 5f, NavMesh.AllAreas)) continue;
                float score = danger ? Flat(hit.position, lastHunterPos) : Random.value;   // đang có nguy hiểm: đi xa chỗ Hunter
                if (score > bestScore) { bestScore = score; wanderPoint = hit.position; }
            }
        }
        SetGoal("Đi dạo");
        bot.Nav.SetDestination(wanderPoint, 1.5f);
    }
}