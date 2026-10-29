using UnityEngine;

public static class PlayerProfile
{
    const string Key = "playerName";
    public const int MaxLength = 16;

    public static string Name
    {
        get => Sanitize(PlayerPrefs.GetString(Key, ""));
        set { PlayerPrefs.SetString(Key, Sanitize(value)); PlayerPrefs.Save(); }
    }

    public static string Sanitize(string s)
    {
        s = (s ?? "").Replace("|", "").Replace("\n", "").Replace("\r", "").Trim();
        return s.Length > MaxLength ? s.Substring(0, MaxLength) : s;
    }
}