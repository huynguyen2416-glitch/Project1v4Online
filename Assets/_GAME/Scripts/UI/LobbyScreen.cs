using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// GIAO DIỆN PHÒNG CHỜ MỚI (dựng hoàn toàn bằng code, dùng uGUI + legacy Text như phần còn lại của dự án).
///
/// Cách dùng: kéo script này vào đúng GameObject "LobbyPanel" hiện có (cái mà MainMenuController và LobbyUI đang bật/tắt).
/// Khi chạy, script ẩn các thành phần cũ của LobbyPanel rồi dựng giao diện mới: thanh trên (Rời phòng, Mã phòng, Sao chép),
/// khung Thiết lập (bản đồ, nhân vật, Hunter, + Thêm bot, Lấp đầy, debug) và khung Phòng chờ 5 chỗ (người, bot, chỗ trống).
/// Mọi nút gọi thẳng LobbyManager / RoomService, không cần kéo tham chiếu nào khác.
/// </summary>
public class LobbyScreen : MonoBehaviour
{
    [SerializeField] LobbyManager lobby;
    [Tooltip("Để trống: dùng font hệ thống (Segoe UI / Arial) hoặc font mặc định của Unity. Kéo font vào đây nếu muốn dùng font riêng.")]
    [SerializeField] Font font;
    [Tooltip("Ẩn các thành phần cũ trong LobbyPanel khi dựng giao diện mới.")]
    [SerializeField] bool hideOldChildren = true;
    [Tooltip("Nhân với mọi cỡ chữ và chiều cao. Tăng nếu Canvas Scaler của bạn có Reference Resolution nhỏ.")]
    [SerializeField] float uiScale = 1f;

    // ---- Bảng màu (theo mẫu) ----
    static readonly Color CDim = new Color(0f, 0f, 0f, 0.6f);
    static readonly Color CCard = new Color32(33, 33, 33, 255);
    static readonly Color CCardBorder = new Color32(58, 58, 58, 255);
    static readonly Color CPanel = new Color32(27, 27, 27, 255);
    static readonly Color CPanelBorder = new Color32(48, 48, 48, 255);
    static readonly Color CInput = new Color32(38, 38, 38, 255);
    static readonly Color CInputBorder = new Color32(85, 85, 85, 255);
    static readonly Color CText = new Color32(240, 240, 240, 255);
    static readonly Color CMuted = new Color32(154, 154, 154, 255);
    static readonly Color CRowEmpty = new Color32(24, 24, 24, 255);
    static readonly Color CRowEmptyBorder = new Color32(52, 52, 52, 255);
    static readonly Color CRowMe = new Color32(4, 32, 63, 255);
    static readonly Color CRowMeBorder = new Color32(14, 74, 138, 255);
    static readonly Color CRowOther = new Color32(32, 32, 32, 255);
    static readonly Color COrange = new Color32(232, 163, 23, 255);
    static readonly Color COrangeBg = new Color32(58, 42, 8, 255);
    static readonly Color CGreen = new Color32(110, 214, 140, 255);
    static readonly Color CGreenBg = new Color32(16, 58, 32, 255);
    static readonly Color CRed = new Color32(229, 115, 115, 255);
    static readonly Color CRedBg = new Color32(59, 23, 23, 255);
    static readonly Color CRedBorder = new Color32(123, 42, 42, 255);
    static readonly Color CBlueChip = new Color32(30, 50, 80, 255);

    Sprite round, box, check;
    Font uiFont;

    // ---- Phần tử cần cập nhật ----
    Text codeText;
    Button copyBtn;
    Text countText;
    Dropdown mapDd, charDd;
    Toggle hunterToggle, debugToggle;
    GameObject hostGroup;
    Button addBotBtn, fillBtn, startBtn, readyBtn;
    Text readyLabel, statusText;
    Row[] rows = new Row[LobbyManager.MaxPlayers];

    class Row
    {
        public Image bg;
        public Outline outline;
        public Text name;
        public Button roleBtn; public Text roleLabel;
        public Button removeBtn;
        public Image badge; public Text badgeLabel;
    }

    bool built, subscribed;
    readonly List<GameObject> oldChildren = new List<GameObject>();
    int builtMapCount = -1, builtCharCount = -1;
    string overrideMsg; float overrideUntil;
    float nextPoll;

    // ------------------------------------------------------------------ vòng đời

    void Awake()
    {
        if (lobby == null) lobby = FindFirstObjectByType<LobbyManager>();
        Build();
    }

    void OnEnable() { Hook(); Refresh(); }

    // LobbyUI (cũ) có thể bật lại các nhóm cũ như hostOnlyGroup: luôn giữ chúng tắt.
    void LateUpdate()
    {
        if (!hideOldChildren) return;
        for (int i = 0; i < oldChildren.Count; i++)
            if (oldChildren[i] != null && oldChildren[i].activeSelf) oldChildren[i].SetActive(false);
    }
    void OnDisable() { Unhook(); }

    void Update()
    {
        if (lobby == null) lobby = FindFirstObjectByType<LobbyManager>();
        if (lobby == null) return;
        if (lobby.IsSpawned && !subscribed) { Hook(); Refresh(); }
        if (!lobby.IsSpawned && subscribed) Unhook();

        if (Time.unscaledTime >= nextPoll)
        {
            nextPoll = Time.unscaledTime + 0.5f;
            Refresh();
        }
    }

    void Hook()
    {
        if (lobby == null || !lobby.IsSpawned || subscribed) return;
        lobby.OnChanged += Refresh;
        lobby.OnMessage += ShowMessage;
        subscribed = true;
    }

    void Unhook()
    {
        if (subscribed && lobby != null)
        {
            lobby.OnChanged -= Refresh;
            lobby.OnMessage -= ShowMessage;
        }
        subscribed = false;
    }

    void ShowMessage(string msg)
    {
        overrideMsg = msg;
        overrideUntil = Time.unscaledTime + 5f;
        Refresh();
    }

    // ------------------------------------------------------------------ dựng giao diện

    int S(int v) { return Mathf.Max(1, Mathf.RoundToInt(v * uiScale)); }

    void Build()
    {
        if (built) return;
        built = true;

        uiFont = font;
        if (uiFont == null) uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Helvetica" }, 16);
        if (uiFont == null) uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        round = MakeRounded(32, 10);
        box = round;
        check = round;

        if (hideOldChildren)
        {
            foreach (Transform c in transform) { oldChildren.Add(c.gameObject); c.gameObject.SetActive(false); }
            Image own = GetComponent<Image>();
            if (own != null) own.enabled = false;
        }

        // Nền mờ phủ toàn màn hình (chặn bấm xuyên xuống).
        RectTransform dim = RT("Dim", transform);
        Stretch(dim);
        Image dimImg = dim.gameObject.AddComponent<Image>();
        dimImg.color = CDim;

        // Thẻ ngoài.
        RectTransform card = RT("LobbyCard", transform);
        card.anchorMin = new Vector2(0.07f, 0.1f);
        card.anchorMax = new Vector2(0.93f, 0.9f);
        card.offsetMin = card.offsetMax = Vector2.zero;
        Rounded(card.gameObject, CCard, CCardBorder, 1.3f);
        VBox(card.gameObject, S(18), S(14));

        BuildTopBar(card);

        RectTransform body = RT("Body", card);
        HBox(body.gameObject, 0, S(16));
        LE(body.gameObject, flexH: 1);

        BuildSetupCard(body);
        BuildRoomCard(body);
    }

    void BuildTopBar(Transform parent)
    {
        RectTransform bar = RT("TopBar", parent);
        HBox(bar.gameObject, 0, S(10), TextAnchor.MiddleCenter);
        LE(bar.gameObject, prefH: S(46));

        Button leave = MakeButton(bar, "Rời phòng", CRedBg, CRedBorder, CRed, S(15), false);
        LE(leave.gameObject, prefW: S(120), prefH: S(44));
        leave.onClick.AddListener(OnLeave);

        RectTransform spacer = RT("Spacer", bar);
        LE(spacer.gameObject, flexW: 1);

        codeText = Label(bar, "Mã phòng:", S(17), CText, FontStyle.Bold, TextAnchor.MiddleRight);
        LE(codeText.gameObject, prefH: S(44));

        copyBtn = MakeButton(bar, "Sao chép", CInput, CInputBorder, CText, S(15), false);
        LE(copyBtn.gameObject, prefW: S(120), prefH: S(44));
        copyBtn.onClick.AddListener(OnCopy);
    }

    void BuildSetupCard(Transform parent)
    {
        RectTransform left = RT("SetupCard", parent);
        Rounded(left.gameObject, CPanel, CPanelBorder, 1.3f);
        VBox(left.gameObject, S(16), S(8));
        LE(left.gameObject, flexW: 0.85f);

        Label(left, "Thiết lập", S(20), CText, FontStyle.Bold, TextAnchor.MiddleLeft);

        Label(left, "Bản đồ (chủ phòng chọn)", S(14), CMuted, FontStyle.Normal, TextAnchor.MiddleLeft);
        mapDd = MakeDropdown(left, "MapDropdown");
        LE(mapDd.gameObject, prefH: S(46));
        mapDd.onValueChanged.AddListener(i => { if (lobby != null) lobby.SetMap(i); });

        Label(left, "Nhân vật", S(14), CMuted, FontStyle.Normal, TextAnchor.MiddleLeft);
        charDd = MakeDropdown(left, "CharacterDropdown");
        LE(charDd.gameObject, prefH: S(46));
        charDd.onValueChanged.AddListener(i => { if (lobby != null) lobby.SetCharacter(i); });

        hunterToggle = MakeToggle(left, "Tôi muốn làm Hunter");
        LE(hunterToggle.gameObject, prefH: S(30));
        hunterToggle.onValueChanged.AddListener(v => { if (lobby != null) lobby.SetWantHunter(v); });

        RectTransform sep = RT("Separator", left);
        Image sepImg = sep.gameObject.AddComponent<Image>();
        sepImg.color = CPanelBorder; sepImg.raycastTarget = false;
        LE(sep.gameObject, prefH: 1);

        // Chỉ host thấy.
        RectTransform host = RT("HostOnly", left);
        VBox(host.gameObject, 0, S(8));
        hostGroup = host.gameObject;

        Label(host, "Chỉ chủ phòng", S(14), CMuted, FontStyle.Normal, TextAnchor.MiddleLeft);

        RectTransform btnRow = RT("BotButtons", host);
        HBox(btnRow.gameObject, 0, S(10), TextAnchor.MiddleLeft);
        LE(btnRow.gameObject, prefH: S(44));
        addBotBtn = MakeButton(btnRow, "+ Thêm bot", CInput, CInputBorder, CText, S(15), false);
        LE(addBotBtn.gameObject, prefW: S(128), prefH: S(42));
        addBotBtn.onClick.AddListener(() => { if (lobby != null) lobby.AddBot(); });
        fillBtn = MakeButton(btnRow, "Lấp đầy", CInput, CInputBorder, CText, S(15), false);
        LE(fillBtn.gameObject, prefW: S(104), prefH: S(42));
        fillBtn.onClick.AddListener(() => { if (lobby != null) lobby.FillWithBots(); });

        debugToggle = MakeToggle(host, "Bắt đầu khi thiếu người (debug)");
        LE(debugToggle.gameObject, prefH: S(26));
        debugToggle.onValueChanged.AddListener(v => { if (lobby != null) lobby.SetDebug(v); });
    }

    void BuildRoomCard(Transform parent)
    {
        RectTransform right = RT("RoomCard", parent);
        Rounded(right.gameObject, CPanel, CPanelBorder, 1.3f);
        VBox(right.gameObject, S(16), S(8));
        LE(right.gameObject, flexW: 1f);

        RectTransform head = RT("Header", right);
        HBox(head.gameObject, 0, S(6), TextAnchor.MiddleLeft);
        LE(head.gameObject, prefH: S(30));
        Label(head, "Phòng chờ", S(20), CText, FontStyle.Bold, TextAnchor.MiddleLeft);
        countText = Label(head, "0/5", S(18), CMuted, FontStyle.Normal, TextAnchor.MiddleLeft);

        for (int i = 0; i < rows.Length; i++) rows[i] = BuildRow(right, i);

        statusText = Label(right, "", S(15), COrange, FontStyle.Normal, TextAnchor.MiddleLeft);
        statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
        statusText.verticalOverflow = VerticalWrapMode.Overflow;
        LE(statusText.gameObject, prefH: S(40), flexH: 1);

        startBtn = MakeButton(right, "Bắt đầu trận", Color.white, Color.white, new Color32(20, 20, 20, 255), S(17), true);
        LE(startBtn.gameObject, prefH: S(52));
        startBtn.onClick.AddListener(() => { if (lobby != null) lobby.StartMatch(); });

        readyBtn = MakeButton(right, "Sẵn sàng", Color.white, Color.white, new Color32(20, 20, 20, 255), S(17), true);
        LE(readyBtn.gameObject, prefH: S(52));
        readyLabel = readyBtn.GetComponentInChildren<Text>();
        readyBtn.onClick.AddListener(() => { if (lobby != null) lobby.ToggleReady(); });
    }

    Row BuildRow(Transform parent, int index)
    {
        var r = new Row();
        RectTransform rt = RT("Slot" + (index + 1), parent);
        r.bg = Rounded(rt.gameObject, CRowEmpty, CRowEmptyBorder, 1.0f);
        r.outline = rt.GetComponent<Outline>();
        HorizontalLayoutGroup h = HBox(rt.gameObject, S(14), S(8), TextAnchor.MiddleLeft);
        h.padding = new RectOffset(S(14), S(10), 0, 0);
        LE(rt.gameObject, prefH: S(48));

        r.name = Label(rt, "", S(16), CText, FontStyle.Bold, TextAnchor.MiddleLeft);
        r.name.supportRichText = true;
        LE(r.name.gameObject, flexW: 1, prefH: S(30));

        r.roleBtn = MakeButton(rt, "Survivor", CBlueChip, CRowMeBorder, CText, S(13), false);
        LE(r.roleBtn.gameObject, prefW: S(96), prefH: S(30));
        r.roleLabel = r.roleBtn.GetComponentInChildren<Text>();
        int slot = index;
        r.roleBtn.onClick.AddListener(() => OnRoleClicked(slot));

        r.removeBtn = MakeButton(rt, "X", CRedBg, CRedBorder, CRed, S(13), false);
        LE(r.removeBtn.gameObject, prefW: S(32), prefH: S(30));
        r.removeBtn.onClick.AddListener(() => OnRemoveClicked(slot));

        RectTransform badgeRt = RT("Badge", rt);
        r.badge = Rounded(badgeRt.gameObject, COrangeBg, COrangeBg, 2.0f);
        r.badge.raycastTarget = false;
        LE(badgeRt.gameObject, prefW: S(96), prefH: S(26));
        r.badgeLabel = Label(badgeRt, "", S(13), COrange, FontStyle.Normal, TextAnchor.MiddleCenter);
        Stretch(r.badgeLabel.rectTransform);
        return r;
    }

    // ------------------------------------------------------------------ cập nhật

    readonly int[] rowBotId = new int[LobbyManager.MaxPlayers];

    void Refresh()
    {
        if (!built || lobby == null || !lobby.IsSpawned) return;

        bool host = lobby.IsServer;
        int humans = lobby.Count, bots = lobby.BotCount, total = humans + bots;
        bool hasMine = lobby.TryGetMine(out LobbyEntry mine);

        // Mã phòng.
        string code = RoomService.Instance != null ? RoomService.Instance.RoomCode : "";
        codeText.text = string.IsNullOrEmpty(code) ? "Phòng LAN (không có mã)" : "Mã phòng: " + code;
        copyBtn.gameObject.SetActive(!string.IsNullOrEmpty(code));

        countText.text = $"{total}/{LobbyManager.MaxPlayers}";

        // Dropdown bản đồ.
        if (builtMapCount != lobby.MapCount)
        {
            var opts = new List<string>();
            for (int i = 0; i < lobby.MapCount; i++) opts.Add(lobby.MapName(i));
            mapDd.ClearOptions(); mapDd.AddOptions(opts);
            builtMapCount = lobby.MapCount;
        }
        mapDd.SetValueWithoutNotify(Mathf.Clamp(lobby.MapIndex.Value, 0, Mathf.Max(0, lobby.MapCount - 1)));
        mapDd.RefreshShownValue();
        mapDd.interactable = host;   // chỉ host chọn bản đồ

        // Dropdown nhân vật.
        if (builtCharCount != lobby.CharacterCount)
        {
            var opts = new List<string>();
            for (int i = 0; i < lobby.CharacterCount; i++) opts.Add(lobby.CharacterName(i));
            charDd.ClearOptions(); charDd.AddOptions(opts);
            builtCharCount = lobby.CharacterCount;
        }
        if (hasMine)
        {
            charDd.SetValueWithoutNotify(Mathf.Clamp(mine.CharacterIndex, 0, Mathf.Max(0, lobby.CharacterCount - 1)));
            charDd.RefreshShownValue();
            hunterToggle.SetIsOnWithoutNotify(mine.WantsHunter);
            hunterToggle.interactable = mine.WantsHunter || !lobby.HunterBotPresent;   // đã có Hunter bot thì không tick được
        }

        // Nhóm chỉ-host.
        hostGroup.SetActive(host);
        addBotBtn.interactable = total < LobbyManager.MaxPlayers;
        fillBtn.interactable = total < LobbyManager.MaxPlayers;
        debugToggle.SetIsOnWithoutNotify(lobby.DebugAnyCount);

        // Danh sách 5 chỗ: người, rồi bot, rồi chỗ trống.
        int r = 0;
        for (int i = 0; i < humans && r < rows.Length; i++, r++)
        {
            LobbyEntry e = lobby.Get(i);
            bool me = lobby.IsMe(e.ClientId);
            bool isHostEntry = e.ClientId == NetworkManager.ServerClientId;
            Row row = rows[r];
            rowBotId[r] = -1;
            SetRowStyle(row, me ? CRowMe : CRowOther, me ? CRowMeBorder : CPanelBorder);
            row.name.text = e.Name.ToString() + (me ? " (bạn)" : "") +
                            (e.WantsHunter ? "  <color=#e57373>Hunter</color>" : "");
            row.name.color = CText;
            row.roleBtn.gameObject.SetActive(false);
            row.removeBtn.gameObject.SetActive(false);
            row.badge.gameObject.SetActive(true);
            if (isHostEntry) SetBadge(row, "Chủ phòng", CGreen, CGreenBg);
            else if (e.Ready) SetBadge(row, "Sẵn sàng", CGreen, CGreenBg);
            else SetBadge(row, "Đang chờ", COrange, COrangeBg);
        }
        for (int i = 0; i < bots && r < rows.Length; i++, r++)
        {
            BotEntry b = lobby.GetBot(i);
            Row row = rows[r];
            rowBotId[r] = b.BotId;
            SetRowStyle(row, CRowOther, CPanelBorder);
            row.name.text = b.Name.ToString();
            row.name.color = CText;
            row.badge.gameObject.SetActive(false);
            row.roleBtn.gameObject.SetActive(true);
            row.roleLabel.text = b.IsHunter ? "Hunter" : "Survivor";
            row.roleBtn.interactable = host;   // host bấm để đổi Survivor <-> Hunter
            row.removeBtn.gameObject.SetActive(host);
        }
        for (; r < rows.Length; r++)
        {
            Row row = rows[r];
            rowBotId[r] = -1;
            SetRowStyle(row, CRowEmpty, CRowEmptyBorder);
            row.name.text = "Chờ người chơi...";
            row.name.color = CMuted;
            row.roleBtn.gameObject.SetActive(false);
            row.removeBtn.gameObject.SetActive(false);
            row.badge.gameObject.SetActive(false);
        }

        // Dòng trạng thái.
        string status = "";
        if (!string.IsNullOrEmpty(overrideMsg) && Time.unscaledTime < overrideUntil) status = overrideMsg;
        else if (!lobby.DebugAnyCount && total < LobbyManager.MaxPlayers)
            status = $"Cần đủ {LobbyManager.MaxPlayers} người ({total}/{LobbyManager.MaxPlayers}). Thêm bot hoặc bật debug.";
        else
        {
            for (int i = 0; i < humans; i++)
            {
                LobbyEntry e = lobby.Get(i);
                if (!e.Ready && e.ClientId != NetworkManager.ServerClientId) { status = "Chờ " + e.Name.ToString() + " sẵn sàng."; break; }
            }
        }
        statusText.text = status;

        // Nút hành động: host bấm Bắt đầu, người khác bấm Sẵn sàng.
        startBtn.gameObject.SetActive(host);
        readyBtn.gameObject.SetActive(!host);
        if (hasMine && readyLabel != null) readyLabel.text = mine.Ready ? "Hủy sẵn sàng" : "Sẵn sàng";
    }

    void SetRowStyle(Row row, Color fill, Color border)
    {
        row.bg.color = fill;
        row.outline.effectColor = border;
    }

    void SetBadge(Row row, string text, Color fg, Color bg)
    {
        row.badgeLabel.text = text;
        row.badgeLabel.color = fg;
        row.badge.color = bg;
    }

    // ------------------------------------------------------------------ hành động

    void OnRoleClicked(int rowIndex)
    {
        if (lobby != null && rowBotId[rowIndex] >= 0) lobby.ToggleBotRole(rowBotId[rowIndex]);
    }

    void OnRemoveClicked(int rowIndex)
    {
        if (lobby != null && rowBotId[rowIndex] >= 0) lobby.RemoveBot(rowBotId[rowIndex]);
    }

    void OnCopy()
    {
        string code = RoomService.Instance != null ? RoomService.Instance.RoomCode : "";
        if (!string.IsNullOrEmpty(code)) GUIUtility.systemCopyBuffer = code;
    }

    async void OnLeave()
    {
        if (RoomService.Instance != null && RoomService.Instance.InRoom)
            await RoomService.Instance.LeaveAsync();
        else if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();
    }

    // ------------------------------------------------------------------ dựng control

    static Sprite MakeRounded(int size, int radius)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[size * size];
        float half = size * 0.5f, inner = half - radius;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - half) - inner;
                float dy = Mathf.Abs(y + 0.5f - half) - inner;
                float d = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        float b = radius + 1;
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
    }

    static RectTransform RT(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    Image Rounded(GameObject go, Color fill, Color border, float ppuMultiplier)
    {
        Image img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.sprite = round;
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = ppuMultiplier;
        img.color = fill;
        Outline o = go.GetComponent<Outline>();
        if (o == null) o = go.AddComponent<Outline>();
        o.effectColor = border;
        o.effectDistance = new Vector2(1f, 1f);
        o.useGraphicAlpha = false;
        return img;
    }

    static VerticalLayoutGroup VBox(GameObject go, int pad, int spacing)
    {
        var v = go.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset(pad, pad, pad, pad);
        v.spacing = spacing;
        v.childAlignment = TextAnchor.UpperLeft;
        v.childControlWidth = true; v.childControlHeight = true;
        v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        return v;
    }

    static HorizontalLayoutGroup HBox(GameObject go, int pad, int spacing, TextAnchor anchor = TextAnchor.UpperLeft)
    {
        var h = go.AddComponent<HorizontalLayoutGroup>();
        h.padding = new RectOffset(pad, pad, pad, pad);
        h.spacing = spacing;
        h.childAlignment = anchor;
        h.childControlWidth = true; h.childControlHeight = true;
        h.childForceExpandWidth = false; h.childForceExpandHeight = anchor == TextAnchor.UpperLeft;
        return h;
    }

    static LayoutElement LE(GameObject go, float prefW = -1, float prefH = -1, float flexW = -1, float flexH = -1)
    {
        LayoutElement le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        if (prefW >= 0) le.preferredWidth = prefW;
        if (prefH >= 0) le.preferredHeight = prefH;
        if (flexW >= 0) le.flexibleWidth = flexW;
        if (flexH >= 0) le.flexibleHeight = flexH;
        return le;
    }

    Text Label(Transform parent, string text, int size, Color color, FontStyle style, TextAnchor anchor)
    {
        RectTransform rt = RT("Text", parent);
        Text t = rt.gameObject.AddComponent<Text>();
        t.font = uiFont;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = color;
        t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    Button MakeButton(Transform parent, string label, Color fill, Color border, Color textColor, int fontSize, bool bold)
    {
        RectTransform rt = RT("Button", parent);
        Image img = Rounded(rt.gameObject, fill, border, 1.3f);
        Button b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        ColorBlock cb = b.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        cb.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        cb.selectedColor = Color.white;
        cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
        b.colors = cb;
        Text t = Label(rt, label, fontSize, textColor, bold ? FontStyle.Bold : FontStyle.Normal, TextAnchor.MiddleCenter);
        Stretch(t.rectTransform);
        b.onClick.AddListener(() => SoundManager.I?.PlayUIClick());
        return b;
    }

    Toggle MakeToggle(Transform parent, string label)
    {
        var res = new DefaultControls.Resources { standard = box, background = box, inputField = box, knob = box, checkmark = check, mask = box };
        GameObject go = DefaultControls.CreateToggle(res);
        go.name = "Toggle";
        go.transform.SetParent(parent, false);
        Toggle tg = go.GetComponent<Toggle>();
        tg.SetIsOnWithoutNotify(false);

        Transform bg = go.transform.Find("Background");
        Image bgImg = bg.GetComponent<Image>();
        bgImg.sprite = box; bgImg.type = Image.Type.Sliced; bgImg.pixelsPerUnitMultiplier = 2.2f; bgImg.color = CInput;
        Outline o = bg.gameObject.AddComponent<Outline>();
        o.effectColor = CInputBorder; o.effectDistance = new Vector2(1f, 1f); o.useGraphicAlpha = false;
        RectTransform bgRt = (RectTransform)bg;
        bgRt.anchorMin = bgRt.anchorMax = new Vector2(0f, 0.5f);
        bgRt.pivot = new Vector2(0f, 0.5f);
        bgRt.anchoredPosition = Vector2.zero;
        bgRt.sizeDelta = new Vector2(S(20), S(20));

        Image mark = bg.Find("Checkmark").GetComponent<Image>();
        mark.sprite = box; mark.type = Image.Type.Sliced; mark.pixelsPerUnitMultiplier = 3.5f; mark.color = new Color32(90, 169, 255, 255);
        RectTransform markRt = mark.rectTransform;
        markRt.anchorMin = markRt.anchorMax = new Vector2(0.5f, 0.5f);
        markRt.anchoredPosition = Vector2.zero;
        markRt.sizeDelta = new Vector2(S(12), S(12));

        Text t = go.GetComponentInChildren<Text>();
        t.font = uiFont; t.text = label; t.fontSize = S(15); t.color = new Color32(230, 230, 230, 255);
        t.alignment = TextAnchor.MiddleLeft; t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
        tr.offsetMin = new Vector2(S(30), 0); tr.offsetMax = Vector2.zero;
        return tg;
    }

    Dropdown MakeDropdown(Transform parent, string name)
    {
        var res = new DefaultControls.Resources { standard = box, background = box, inputField = box, knob = box, checkmark = check, mask = box };
        GameObject go = DefaultControls.CreateDropdown(res);
        go.name = name;
        go.transform.SetParent(parent, false);

        foreach (Text t in go.GetComponentsInChildren<Text>(true))
        {
            t.font = uiFont; t.color = CText; t.fontSize = S(16);
        }

        Image img = go.GetComponent<Image>();
        img.sprite = box; img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1.3f; img.color = CInput;
        Outline o = go.AddComponent<Outline>();
        o.effectColor = CInputBorder; o.effectDistance = new Vector2(1f, 1f); o.useGraphicAlpha = false;

        Dropdown dd = go.GetComponent<Dropdown>();
        dd.captionText.alignment = TextAnchor.MiddleLeft;
        RectTransform cap = dd.captionText.rectTransform;
        cap.offsetMin = new Vector2(S(14), cap.offsetMin.y);

        // Mũi tên: thay ảnh mặc định bằng chữ "v".
        Transform arrow = go.transform.Find("Arrow");
        if (arrow != null)
        {
            arrow.GetComponent<Image>().enabled = false;
            Text v = Label(go.transform, "v", S(15), CText, FontStyle.Bold, TextAnchor.MiddleRight);
            Stretch(v.rectTransform);
            v.rectTransform.offsetMax = new Vector2(-S(14), 0);
        }

        // Danh sách xổ xuống.
        Transform tpl = go.transform.Find("Template");
        if (tpl != null)
        {
            Image timg = tpl.GetComponent<Image>();
            timg.sprite = box; timg.type = Image.Type.Sliced; timg.pixelsPerUnitMultiplier = 1.3f; timg.color = CInput;
            Outline to = tpl.gameObject.AddComponent<Outline>();
            to.effectColor = CInputBorder; to.effectDistance = new Vector2(1f, 1f); to.useGraphicAlpha = false;
            ((RectTransform)tpl).sizeDelta = new Vector2(0, S(160));

            Transform item = tpl.Find("Viewport/Content/Item");
            if (item != null)
            {
                ((RectTransform)item).sizeDelta = new Vector2(0, S(36));
                Image itemBg = item.Find("Item Background").GetComponent<Image>();
                itemBg.color = new Color32(48, 48, 48, 255);
                Transform itemCheck = item.Find("Item Checkmark");
                if (itemCheck != null) itemCheck.GetComponent<Image>().color = new Color32(90, 169, 255, 255);
                Text il = item.Find("Item Label").GetComponent<Text>();
                il.alignment = TextAnchor.MiddleLeft;
            }
            Transform sb = tpl.Find("Scrollbar");
            if (sb != null) sb.gameObject.SetActive(false);
            ScrollRect sr = tpl.GetComponent<ScrollRect>();
            if (sr != null) sr.verticalScrollbar = null;
        }
        return dd;
    }
}
