using System.Collections.Generic;

/// <summary>
/// Server nhớ nhân vật mỗi người đã chọn ở phòng chờ (chỉ số trong danh sách prefab của vai họ nhận).
/// LobbyManager ghi khi bấm Bắt đầu, MatchSpawner đọc khi spawn. Giống RoleAssignments.
/// </summary>
public static class CharacterAssignments
{
    static readonly Dictionary<ulong, int> map = new Dictionary<ulong, int>();

    public static void Clear() => map.Clear();
    public static void Set(ulong clientId, int characterIndex) => map[clientId] = characterIndex;
    public static int Get(ulong clientId) => map.TryGetValue(clientId, out int i) ? i : 0;
}
