namespace SoakTest;

/// <summary>
/// 소크 테스트 티켓팅 경합 패턴을 정의합니다.
/// </summary>
internal enum ContentionPattern
{
    /// <summary>항상 그리드 앞 K석 → 최대 CAS 충돌, 서버측 대부분 SeatTaken.</summary>
    Hotspot,

    /// <summary>
    /// 클라이언트 ID·사이클 번호 기반 K석 연속 블록 → 부하 분산, 높은 confirm율.
    /// 사이클마다 K씩 전진해 순환합니다.
    /// </summary>
    Spread,

    /// <summary>
    /// 전 그리드를 1석씩 공격적으로 회전 → 매진 방향 압박.
    /// TTL 만료·abandon으로 반납된 좌석이 재경합됩니다.
    /// </summary>
    Grind,
}

/// <summary>
/// 경합 패턴별 좌석 선택 순수 함수 모음입니다.
/// 상태 없이 (pattern, 그리드 정보, clientId, cycleIndex)만으로 K석 좌표를 결정합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. 모든 메서드는 순수 함수입니다(외부 상태 없음).
/// <b>[Memory:]</b> byte[] 반환 — K원소 소규모 배열(최대 4B×2=8B). 저빈도 경로(사이클당 1회) 허용.
/// <b>[Blocking:]</b> Non-blocking. 즉시 반환.
/// </remarks>
internal static class SeatPicker
{
    /// <summary>
    /// 경합 패턴에 따라 K석의 (row, col) 좌표 배열을 반환합니다.
    /// </summary>
    /// <param name="pattern">좌석 선택 패턴입니다.</param>
    /// <param name="totalRows">그리드 행 수입니다.</param>
    /// <param name="totalCols">그리드 열 수입니다.</param>
    /// <param name="k">선택할 좌석 수입니다(≤ MaxSeatsPerSession, ≤ totalRows*totalCols).</param>
    /// <param name="clientId">클라이언트 인덱스입니다(0-based, 좌석 오프셋 계산에 사용).</param>
    /// <param name="cycleIndex">누적 사이클 번호입니다(좌석 오프셋 순환에 사용).</param>
    /// <returns>
    /// <c>(Rows, Cols)</c> 튜플. 두 배열의 길이는 항상 같으며 <c>min(k, total)</c>입니다.
    /// <c>seatId = Rows[i] * totalCols + Cols[i]</c>로 서버 내부 평면 인덱스와 일치합니다.
    /// </returns>
    public static (byte[] Rows, byte[] Cols) Pick(
        ContentionPattern pattern,
        int totalRows, int totalCols,
        int k, int clientId, int cycleIndex)
    {
        int total = totalRows * totalCols;
        k = Math.Min(k, total); // k > total 시 전체 좌석 수로 clamp

        var rows = new byte[k];
        var cols = new byte[k];

        // 시작 seatId 산출 (패턴별)
        int startSeat = pattern switch
        {
            ContentionPattern.Hotspot =>
                // 항상 seatId 0 → K석 연속: 모든 클라이언트가 같은 블록을 공격 → 최대 CAS 충돌
                0,

            ContentionPattern.Spread =>
                // offset = (clientId * K + cycle * K) % total
                // K석 블록이 사이클마다 K씩 전진: 클라이언트가 전 좌석에 걸쳐 분산됨
                (clientId * k + cycleIndex * k) % total,

            ContentionPattern.Grind =>
                // offset = (clientId * K + cycle) % total
                // 1석씩 전진: 가장 공격적인 회전으로 빈 좌석을 즉시 재공격
                (clientId * k + cycleIndex) % total,

            _ => 0,
        };

        for (int i = 0; i < k; i++)
        {
            // wrap-around: total로 모듈로 → 범위 초과 방지
            int seatId = (startSeat + i) % total;
            // seatId → (row, col): server 내부 변환과 동일하게 totalCols로 나눔
            rows[i] = (byte)(seatId / totalCols);
            cols[i] = (byte)(seatId % totalCols);
        }

        return (rows, cols);
    }
}
