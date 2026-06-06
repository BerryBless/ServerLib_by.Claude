namespace StabilityTest;

/// <summary>서버 [STATS] 라인 1개의 스냅샷. 모든 값은 누적/순간값입니다.</summary>
public readonly record struct StatsSnapshot(long Received, long Test, int Sessions, long HeapBytes, int Gen2);

/// <summary>서버 stdout의 <c>[STATS]</c> 라인을 <see cref="StatsSnapshot"/>으로 파싱합니다.</summary>
public static class StatsLineParser
{
    private const string Marker = "[STATS]";

    /// <summary><paramref name="line"/>에서 [STATS] 토큰 이후 5개 키를 모두 파싱하면 true.</summary>
    public static bool TryParse(string line, out StatsSnapshot snapshot)
    {
        snapshot = default;
        if (string.IsNullOrEmpty(line)) return false;
        int idx = line.IndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0) return false;

        var tokens = line[(idx + Marker.Length)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        long received = 0, test = 0, heap = 0;
        int sessions = 0, gen2 = 0;
        bool hasR = false, hasT = false, hasS = false, hasH = false, hasG = false;

        foreach (var tok in tokens)
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            var key = tok[..eq];
            var val = tok[(eq + 1)..];
            switch (key)
            {
                case "received": hasR = long.TryParse(val, out received); break;
                case "test": hasT = long.TryParse(val, out test); break;
                case "sessions": hasS = int.TryParse(val, out sessions); break;
                case "heapBytes": hasH = long.TryParse(val, out heap); break;
                case "gen2": hasG = int.TryParse(val, out gen2); break;
            }
        }

        if (!(hasR && hasT && hasS && hasH && hasG)) return false;
        snapshot = new StatsSnapshot(received, test, sessions, heap, gen2);
        return true;
    }
}
