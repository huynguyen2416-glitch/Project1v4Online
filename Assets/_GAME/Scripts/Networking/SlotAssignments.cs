using System.Collections.Generic;

/// <summary>Server chia chỗ đứng (slot 0..4) cho từng người và bot lúc bắt đầu trận, để không ai spawn chồng lên nhau.</summary>
public static class SlotAssignments
{
    static readonly Dictionary<ulong, int> map = new Dictionary<ulong, int>();

    public static void Clear() => map.Clear();
    public static void Set(ulong clientId, int slot) => map[clientId] = slot;
    public static bool TryGet(ulong clientId, out int slot) => map.TryGetValue(clientId, out slot);
}
