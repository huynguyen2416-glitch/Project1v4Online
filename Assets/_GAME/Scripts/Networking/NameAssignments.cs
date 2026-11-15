using System.Collections.Generic;

/// <summary>Server nhớ tên của từng clientId. ConnectionGate ghi lúc duyệt kết nối (cả người nối lại với clientId mới).</summary>
public static class NameAssignments
{
    static readonly Dictionary<ulong, string> map = new Dictionary<ulong, string>();

    public static void Set(ulong clientId, string name) =>
        map[clientId] = string.IsNullOrWhiteSpace(name) ? Fallback(clientId) : name;

    public static string Get(ulong clientId) =>
        map.TryGetValue(clientId, out string n) ? n : Fallback(clientId);

    public static void Clear() => map.Clear();

    // Người chưa đặt tên: host là "Người chơi 1", client tiếp theo là "Người chơi 2"...
    static string Fallback(ulong clientId) => "Người chơi " + (clientId + 1);
}
