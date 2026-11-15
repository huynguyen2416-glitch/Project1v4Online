using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HUD trạng thái Survivor: mỗi Survivor một ô (khung + tên + trạng thái). Mọi máy (Hunter lẫn Survivor) đều thấy.
/// Chỉ ĐỌC dữ liệu đã đồng bộ sẵn (SurvivorState.Current, PlayerIdentity.PlayerName, RocketChair.TimeLeft),
/// không thêm NetworkVariable hay RPC nào. Tự dựng ô bằng code nên không cần prefab.
/// Gắn vào Canvas của scene Match (cùng chỗ với MatchHUD).
/// </summary>
public class SurvivorStatusHUD : MonoBehaviour
{
    [Header("Vị trí (góc trên phải Canvas; offset = khoảng cách tới mép)")]
    [SerializeField] Vector2 offset = new Vector2(20f, 20f);
    [SerializeField] Vector2 slotSize = new Vector2(110f, 150f);
    [SerializeField] float spacing = 12f;

    [Header("Tùy chọn")]
    [Tooltip("Ảnh chân dung theo thứ tự ô (ô 1, 2, 3...). Để trống = ô tô màu theo trạng thái.")]
    [SerializeField] Sprite[] portraits;
    [Tooltip("Font cho chữ (nên kéo cùng font với MatchHUD để hiện đủ dấu tiếng Việt). Trống = font mặc định của Unity.")]
    [SerializeField] Font font;

    class Slot
    {
        public SurvivorState state;
        public RectTransform root;
        public Image portrait;
        public Text nameText;
        public Text statusText;
        public string shownName, shownStatus;
        public Color shownColor;
        public bool hasSprite;
    }

    readonly List<Slot> slots = new List<Slot>();
    RectTransform container;
    float nextRefresh;

    void Awake()
    {
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var go = new GameObject("SurvivorStatusList", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        container = go.GetComponent<RectTransform>();
        container.SetParent(transform, false);
        container.anchorMin = container.anchorMax = container.pivot = new Vector2(1f, 1f);
        container.anchoredPosition = new Vector2(-offset.x, -offset.y);
        container.sizeDelta = new Vector2((slotSize.x + spacing) * 8f, slotSize.y);

        var h = go.GetComponent<HorizontalLayoutGroup>();
        h.spacing = spacing;
        h.childAlignment = TextAnchor.UpperRight;
        h.childControlWidth = h.childControlHeight = false;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
    }

    void Update()
    {
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.2f;

        List<SurvivorState> list = SurvivorState.All;
        bool same = list.Count == slots.Count;
        for (int i = 0; same && i < slots.Count; i++)
            if (slots[i].state != list[i]) same = false;
        if (!same) Rebuild(list);

        foreach (Slot s in slots) Refresh(s);
    }

    void Rebuild(List<SurvivorState> list)
    {
        foreach (Slot s in slots)
            if (s.root != null) Destroy(s.root.gameObject);
        slots.Clear();
        for (int i = 0; i < list.Count; i++) slots.Add(CreateSlot(list[i], i));
    }

    Slot CreateSlot(SurvivorState st, int index)
    {
        var slot = new Slot { state = st };

        var rootGo = new GameObject("Slot_" + index, typeof(RectTransform), typeof(Image));
        slot.root = rootGo.GetComponent<RectTransform>();
        slot.root.SetParent(container, false);
        slot.root.sizeDelta = slotSize;
        Image bg = rootGo.GetComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.55f);
        bg.raycastTarget = false;

        var pGo = new GameObject("Portrait", typeof(RectTransform), typeof(Image));
        RectTransform pr = pGo.GetComponent<RectTransform>();
        pr.SetParent(slot.root, false);
        pr.anchorMin = new Vector2(0f, 1f);
        pr.anchorMax = new Vector2(1f, 1f);
        pr.pivot = new Vector2(0.5f, 1f);
        pr.sizeDelta = new Vector2(-12f, slotSize.x - 12f);
        pr.anchoredPosition = new Vector2(0f, -6f);
        slot.portrait = pGo.GetComponent<Image>();
        slot.portrait.raycastTarget = false;
        if (portraits != null && index < portraits.Length && portraits[index] != null)
        {
            slot.portrait.sprite = portraits[index];
            slot.hasSprite = true;
        }

        slot.nameText = MakeText(slot.root, "Name", 14, 22f, 24f);
        slot.statusText = MakeText(slot.root, "Status", 13, 24f, 0f);
        slot.shownColor = new Color(-1f, -1f, -1f, -1f);   // ép Refresh gán màu lần đầu
        return slot;
    }

    Text MakeText(Transform parent, string objName, int size, float height, float y)
    {
        var go = new GameObject(objName, typeof(RectTransform), typeof(Text));
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(-8f, height);
        rt.anchoredPosition = new Vector2(0f, y);

        Text t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        t.color = Color.white;
        t.raycastTarget = false;
        return t;
    }

    void Refresh(Slot s)
    {
        if (s.state == null) return;

        PlayerIdentity id = s.state.GetComponent<PlayerIdentity>();
        string shown = (id != null && !string.IsNullOrEmpty(id.DisplayName)) ? id.DisplayName : "Survivor";
        if (s.state.IsOwner) shown += " (bạn)";

        SurvivorHealth h = s.state.Current;
        string status = StatusText(s.state, h);
        Color col = StatusColor(h);

        if (shown != s.shownName) { s.shownName = shown; s.nameText.text = shown; }
        if (status != s.shownStatus) { s.shownStatus = status; s.statusText.text = status; }
        if (col != s.shownColor)
        {
            s.shownColor = col;
            s.statusText.color = col;
            // Có ảnh chân dung thì chỉ làm xám khi đã rời trận; chưa có ảnh thì tô màu theo trạng thái.
            if (s.hasSprite)
                s.portrait.color = (h == SurvivorHealth.Escaped || h == SurvivorHealth.Eliminated)
                    ? new Color(0.45f, 0.45f, 0.45f, 1f) : Color.white;
            else
                s.portrait.color = new Color(col.r, col.g, col.b, 0.6f);
        }
    }

    static string StatusText(SurvivorState st, SurvivorHealth h)
    {
        switch (h)
        {
            case SurvivorHealth.Healthy: return "Khỏe";
            case SurvivorHealth.Injured: return "Bị thương";
            case SurvivorHealth.Downed: return "Gục";
            case SurvivorHealth.Carried: return "Bị bế";
            case SurvivorHealth.Escaped: return "Đã thoát";
            case SurvivorHealth.Eliminated: return "Bị loại";
            case SurvivorHealth.OnChair:
                {
                    // Cùng cách RocketChair.ServerPlace xác định người ngồi: bot dùng OriginalClientId.
                    BotController bc = st.GetComponent<BotController>();
                    ulong victimId = (bc != null && bc.OriginalClientId != 0) ? bc.OriginalClientId : st.OwnerClientId;
                    foreach (RocketChair chair in RocketChair.All)
                        if (chair != null && chair.OccupantId.Value == victimId)
                            return "Trên ghế " + Mathf.CeilToInt(chair.TimeLeft.Value) + "s";
                    return "Trên ghế";
                }
            default: return "";
        }
    }

    static Color StatusColor(SurvivorHealth h)
    {
        switch (h)
        {
            case SurvivorHealth.Healthy: return new Color(0.35f, 0.85f, 0.40f);
            case SurvivorHealth.Injured: return new Color(1.00f, 0.65f, 0.20f);
            case SurvivorHealth.Downed: return new Color(0.95f, 0.25f, 0.25f);
            case SurvivorHealth.OnChair: return new Color(0.80f, 0.30f, 0.90f);
            case SurvivorHealth.Carried: return new Color(1.00f, 0.40f, 0.70f);
            case SurvivorHealth.Escaped: return new Color(0.50f, 0.75f, 1.00f);
            default: return new Color(0.45f, 0.45f, 0.45f);   // Eliminated
        }
    }
}