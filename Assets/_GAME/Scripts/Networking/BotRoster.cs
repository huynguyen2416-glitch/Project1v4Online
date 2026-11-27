using System.Collections.Generic;

/// <summary>
/// Danh sách bot của trận (chỉ server dùng). Bot không có kết nối thật nên được cấp clientId GIẢ (>= BaseId).
/// Các bảng RoleAssignments / CharacterAssignments / NameAssignments / SlotAssignments đều dùng id giả này.
/// </summary>
public static class BotRoster
{
    public const ulong BaseId = 10000UL;
    static readonly List<ulong> ids = new List<ulong>();

    public static IReadOnlyList<ulong> Ids => ids;
    public static void Clear() => ids.Clear();
    public static void Add(ulong id) { if (!ids.Contains(id)) ids.Add(id); }
    public static bool IsBot(ulong id) => id >= BaseId;
    public static ulong IdOf(int botId) => BaseId + (ulong)botId;
}
