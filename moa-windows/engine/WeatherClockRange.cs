using System.Text.RegularExpressions;

namespace VoltiEngine;

public sealed record WeatherClockRange(int StartHour, int EndHour, int DayOffset, int? AlternativeHour = null, DateOnly? SelectedDate = null)
{
    // EndHour can exceed 23 to represent a range crossing midnight.
    private static readonly Regex Pattern = new(@"^(?<place>.*?)\s*(?<ap1>오전|오후)?\s*(?<h1>\d{1,2})\s*(?:시\s*(?:(?<m1>\d{1,2})\s*분)?|:(?<m1>\d{2}))?\s*(?:~|～|〜|–|—|-|부터)\s*(?<ap2>오전|오후)?\s*(?<h2>\d{1,2})\s*(?:시\s*(?:(?<m2>\d{1,2})\s*분)?|:(?<m2>\d{2}))?\s*(?:까지)?\s*(?<question>.*)$");
    private static readonly Regex Single = new(@"^(?<place>.*?)\s*(?<ap1>오전|오후|아침|저녁|밤)?\s*(?<h1>\d{1,2})\s*(?:시(?!간)\s*(?:(?<m1>\d{1,2})\s*분)?|(?<colon>:)(?<m1>\d{2}))\s*(?:에는|에|의)?\s*(?<question>.*)$");
    private static readonly string[] KoreanHours = ["", "한", "두", "세", "네", "다섯", "여섯", "일곱", "여덟", "아홉", "열", "열한", "열두"];
    public const string Help = "시간을 ‘안성 오후 9시 날씨’, ‘오늘 12시~15시 온도’ 또는 ‘내일 오전 9시부터 오후 3시까지 날씨’처럼 적어 줘. 정시 기준으로 최대 24시간 범위를 조회할 수 있어요.";
    public const string PeriodHelp = "오전인지 오후인지 알려줘요. 예: ‘오후 1시~3시 온도’";

    public static (WeatherClockRange? Range, string Region, string? Error, bool Matched) Parse(string text, bool weatherContext)
    {
        if (text.StartsWith("/검색") || Regex.IsMatch(text, @"(?:검색|조사)해\s*(?:줘요?|주세요)[?.!\s]*$")) return (null, "", null, false);
        text = Regex.Replace(text, @"(?<hour>열두|열한|아홉|여덟|일곱|여섯|다섯|열|네|세|두|한)\s*시(?!간)", m => Array.IndexOf(KoreanHours, m.Groups["hour"].Value) + "시");
        if (text.StartsWith("/날씨 ")) text = text[4..].Trim() + " 날씨";
        var match = Pattern.Match(text);
        bool single = !match.Success;
        if (single) match = Single.Match(text);
        var prefix = match.Groups["place"].Value;
        var question = match.Groups["question"].Value;
        bool weatherBefore = single && Regex.IsMatch(prefix, @"날씨|온도|기온") && Regex.IsMatch(question, @"^(?:알려\s*줘요?|알려주세요|어때요?|은|는)?[?.!\s]*$");
        if (!match.Success || !(Regex.IsMatch(match.Groups["question"].Value, @"날씨|온도|기온|몇\s*도") ||
            weatherBefore || weatherContext && Regex.IsMatch(question, @"^(?:은|는)?[?!.\s]*$"))) return (null, "", null, false);
        if (single && Regex.IsMatch(question, @"^(?:쯤|부터|까지|~|-)")) return (null, "", Help, true);
        if (weatherBefore) prefix = Regex.Replace(prefix, @"날씨|온도|기온", "");
        if (Regex.IsMatch(prefix, @"모레|어제|다음|지난")) return (null, "", "오늘·내일의 시간별 예보를 볼 수 있어요. " + Help, true);
        var days = Regex.Matches(prefix, @"오늘|내일").Select(m => m.Value).Distinct().ToArray();
        if (days.Length > 1) return (null, "", Help, true);
        var region = Regex.Replace(prefix, @"모아야?[, ]*|오늘|내일", "").Trim();
        region = Regex.Replace(region, @"(?:에서|의|은|는|에)$", "").Trim();
        var start = int.Parse(match.Groups["h1"].Value);
        var end = single ? start : int.Parse(match.Groups["h2"].Value);
        var ap1 = match.Groups["ap1"].Value;
        var ap2 = match.Groups["ap2"].Value;
        bool midnight = single && (ap1 == "밤" && start == 12 || ap1.Length == 0 && start == 24);
        if (ap1 == "아침") ap1 = "오전";
        if (ap1 == "밤") ap1 = start is >= 1 and <= 5 || start == 12 ? "오전" : "오후";
        if (ap1 == "저녁") ap1 = "오후";
        foreach (var minute in new[] { "m1", "m2" })
            if (match.Groups[minute].Success && int.Parse(match.Groups[minute].Value) != 0)
                return (null, region, "정시 단위 예보라 분 단위 온도는 확인할 수 없어요. " + Help, true);
        if (start > 23 && !(single && start == 24 && ap1.Length == 0) || end > 24 || ap1.Length > 0 && start is < 1 or > 12 || ap2.Length > 0 && end is < 1 or > 12)
            return (null, region, Help, true);
        static int Convert(int h, string ap) => ap == "오전" ? h % 12 : ap == "오후" ? h % 12 + 12 : h;
        start = Convert(start, ap1);
        if (single)
        {
            if (midnight) start = 0;
            // Bare Korean '9시' is ambiguous. Fetch both real readings, without
            // guessing AM/PM or treating the time as part of the city name.
            int? alternative = ap1.Length == 0 && !match.Groups["colon"].Success && start is >= 1 and <= 11 ? start + 12 : null;
            return (new(start, start, (days.Contains("내일") ? 1 : 0) + (midnight ? 1 : 0), alternative), region, null, true);
        }
        end = Convert(end, ap2);
        if (ap2.Length == 0 && ap1 == "오후" && end is >= 1 and <= 12) end = end % 12 + 12;
        // Korean shorthand "12시~3시" normally means noon through 15:00.
        if (ap1.Length == 0 && ap2.Length == 0 && start == 12 && end is >= 1 and <= 11) end += 12;
        if (ap1.Length == 0 && ap2.Length == 0 && start is >= 1 and <= 11 && end is >= 1 and <= 11)
            return (null, region, PeriodHelp, true);
        if (end < start) end += 24;
        if (end - start >= 24) return (null, region, Help, true);
        return (new(start, end, days.Contains("내일") ? 1 : 0), region, null, true);
    }
}
