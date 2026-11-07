using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoltiEngine;

// Only a city coordinate or an explicitly requested search term leaves the PC.
public sealed class OnlineLookup(HttpClient? client = null, TimeProvider? clock = null)
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(9), MaxResponseContentBufferSize = 96 * 1024,
        DefaultRequestHeaders = { { "User-Agent", "VoltiLocal/0.3 (personal desktop assistant)" } }
    };
    // Chat requests are serialized by the engine. Keep at most 64 recent forecasts.
    private readonly Dictionary<string, (DateTimeOffset Time, JsonElement Data)> forecasts = new();
    private static readonly Regex WeatherWords = new(@"^(?:모아야?[, ]+)?(?<place>[^/\r\n]{0,80}?)\s*날씨(?:가|는|를)?\s*(?:어때요?|어떤가요|알려\s*줘요?|알려주세요|좀\s*알려줘)?[?.!\s]*$");
    private static readonly Regex DayWord = new(@"오늘|내일|현재|지금|모레|어제|다음\s*주");
    private static readonly Regex SearchWords = new(@"^(?<term>.+?)\s+(?:검색|조사)해\s*(?:줘요?|주세요)[?.!\s]*$");
    private static readonly Regex HourlyWords = new(@"^(?<place>.*?)\s*(?:앞으로|향후)?\s*(?<hours>\d{1,3})\s*시간\s*(?<range>동안|간|뒤|후)?(?<question>.*)$");
    private static readonly Regex TemperatureWords = new(@"날씨|기온|온도|몇\s*도");
    private static readonly Regex ClockCorrection = new(@"^지금\s*(?:오전\s*|오후\s*)?\d{1,2}\s*시(?:\s*\d{1,2}\s*분)?\s*(?:인데요?|잖아|아니야|맞아)[?.!\s]*$");
    private const string WeatherHelp = "어느 지역 날씨가 궁금해요? ‘안성 날씨 알려줘’, ‘경기도 안성 내일 날씨’처럼 물어봐. 오늘·내일 예보를 볼 수 있어요.";

    public async Task<string?> TryHandle(string text, CancellationToken token, SkillContext? context = null, bool? wasWeather = null)
    {
        context ??= new();
        bool previousWeather = wasWeather ?? context.LastWeather;
        context.LastWeather = false;
        var pendingTime = context.PendingWeatherTime;
        context.PendingWeatherTime = null;
        var periodFollowup = previousWeather ? Regex.Match(text, @"^(?:그럼\s*)?(?<period>오전|오후)(?:은|는)?[?.!\s]*$") : Match.Empty;
        if (periodFollowup.Success)
        {
            if (pendingTime is not null)
                text = new Regex(@"\d{1,2}\s*(?=시|:|~|～|〜|–|—|-|부터)").Replace(pendingTime, m => periodFollowup.Groups["period"].Value + " " + m.Value, 1);
            else if (context.WeatherClock is not { } lastClock || lastClock.StartHour != lastClock.EndHour)
            { context.LastWeather = true; return "몇 시가 궁금해요? 예: ‘오후 9시 날씨’"; }
            else
            {
                int hour = lastClock.StartHour % 12;
                text = periodFollowup.Groups["period"].Value + (hour == 0 ? 12 : hour) + "시 날씨";
            }
        }
        var clockRange = WeatherClockRange.Parse(text, previousWeather);
        var clockDayFollowup = Regex.Match(text, @"^(?:그럼\s*)?(?<day>오늘|내일)(?:은|는)?[?.!\s]*$");
        if (previousWeather && context.WeatherClock is { } priorClock && clockDayFollowup.Success)
            clockRange = (priorClock with { DayOffset = clockDayFollowup.Groups["day"].Value == "내일" ? 1 : 0, SelectedDate = null }, "", null, true);
        if (clockRange.Matched)
        {
            if (clockRange.Error is not null)
            {
                if (clockRange.Error == WeatherClockRange.PeriodHelp) context.PendingWeatherTime = text;
                context.LastWeather = previousWeather || context.PendingWeatherTime is not null;
                return clockRange.Error;
            }
            var resolved = clockRange.Region.Length == 0 ? (Place: context.Weather, Error: (string?)null) : await Resolve(clockRange.Region, token);
            if (resolved.Error is not null) { ClearWeatherPlace(context); return resolved.Error; }
            if (resolved.Place is null) return WeatherHelp;
            var requestedRange = clockRange.Range!;
            if (previousWeather && clockRange.Region.Length == 0 && !Regex.IsMatch(text, @"오늘|내일") && context.WeatherDate is not null)
                requestedRange = requestedRange with { SelectedDate = context.WeatherDate };
            var answer = await HourlyForecast(resolved.Place, 0, token, requestedRange, context);
            context.Weather = resolved.Place; context.LastWeather = true;
            return answer;
        }
        var hourly = HourlyWords.Match(text);
        bool hourlyRequest = hourly.Success && (TemperatureWords.IsMatch(hourly.Groups["question"].Value) ||
            previousWeather && Regex.IsMatch(hourly.Groups["question"].Value, @"^(?:은|는)?[?.!\s]*$"));
        bool correction = previousWeather && ClockCorrection.IsMatch(text);
        if (hourlyRequest || correction)
        {
            int hours = hourlyRequest ? int.Parse(hourly.Groups["hours"].Value, CultureInfo.InvariantCulture) : context.WeatherHours;
            if (hours is < 1 or > 24) return "시간별 예보는 앞으로 1~24시간까지 볼 수 있어요. 예: 안성 앞으로 3시간 날씨";
            var region = hourlyRequest ? Regex.Replace(hourly.Groups["place"].Value, @"(?:모아야?[, ]*)|앞으로|향후|오늘|현재|지금", "").Trim() : "";
            var place = context.Weather;
            if (region.Length > 0)
            {
                var resolved = await Resolve(region, token);
                if (resolved.Error is not null) { ClearWeatherPlace(context); return resolved.Error; }
                place = resolved.Place;
            }
            if (place is null) return WeatherHelp;
            var answer = await HourlyForecast(place, hours, token, context: context);
            context.Weather = place; context.WeatherHours = hours; context.LastWeather = true;
            return (correction ? "지금 기준으로 다시 봤어요.\n" : "") + answer;
        }
        bool weatherCommand = text == "/날씨" || text.StartsWith("/날씨 ");
        var weatherMatch = WeatherWords.Match(text);
        bool dayFollowup = previousWeather && Regex.IsMatch(text, @"^(?:그럼\s*)?(오늘|내일)(?:은|는)?[?.!\s]*$");
        if (weatherCommand || weatherMatch.Success || dayFollowup)
        {
            var place = weatherCommand ? text[3..].Trim() : dayFollowup ? text : weatherMatch.Groups["place"].Value.Trim();
            var days = DayWord.Matches(place).Select(m => m.Value).Distinct().ToArray();
            if (days.Length > 1 || days.Any(d => d is not ("오늘" or "내일" or "현재" or "지금"))) return WeatherHelp;
            var region = DayWord.Replace(place, "").Trim();
            region = Regex.Replace(region, @"(?:의|은|는)[?.!\s]*$", "").Trim(' ', '?', '.', '!');
            if (dayFollowup) region = "";
            if (region.Length > 80 || region is "여기" or "우리동네" or "우리 동네") return WeatherHelp;
            var resolved = region.Length == 0 ? (Place: context.Weather, Error: (string?)null) : await Resolve(region, token);
            if (resolved.Error is not null) { ClearWeatherPlace(context); return resolved.Error; }
            if (resolved.Place is null) return WeatherHelp;
            var answer = await Forecast(resolved.Place, days.Contains("내일"), token, context);
            context.Weather = resolved.Place; context.LastWeather = true;
            return answer;
        }
        // Weather-shaped requests that we cannot parse must not become model promises to look things up.
        if (!text.StartsWith("/검색") && !SearchWords.IsMatch(text) && TemperatureWords.IsMatch(text) &&
            (previousWeather || Regex.IsMatch(text, @"\d+\s*(?:시|:)|오늘|내일|현재|지금")))
        {
            context.LastWeather = previousWeather;
            return WeatherClockRange.Help;
        }
        string term;
        if (text == "/검색" || text.StartsWith("/검색 ")) term = text[3..].Trim();
        else
        {
            var search = SearchWords.Match(text);
            if (!search.Success) return null;
            term = search.Groups["term"].Value.Trim();
        }
        if (term.Length is < 1 or > 80) return "찾아볼 주제를 80자 안으로 적어 줘. 예를 들면 ‘/검색 오로라’예요.";
        var uri = "https://ko.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrnamespace=0&gsrlimit=1&prop=extracts&explaintext=1&exintro=1&exchars=220&gsrsearch=" + Uri.EscapeDataString(term);
        var result = await Fetch(uri, token);
        if (!result.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages) || pages.GetArrayLength() == 0)
            return "한국어 위키백과에서 찾지 못했어요. 다른 검색어로 찾아볼까?";
        var page = pages[0];
        return $"{page.GetProperty("title").GetString()}\n{page.GetProperty("extract").GetString()}\n출처: 한국어 위키백과 · https://ko.wikipedia.org/?curid={page.GetProperty("pageid").GetInt32()}";
    }

    private async Task<(WeatherPlace? Place, string? Error)> Resolve(string region, CancellationToken token)
    {
        var places = KoreanPlaces.Find(region);
        if (places.Length == 0)
        {
            var data = await Fetch("https://geocoding-api.open-meteo.com/v1/search?count=5&language=ko&format=json&name=" + Uri.EscapeDataString(region), token);
            if (data.TryGetProperty("results", out var results))
                places = results.EnumerateArray().Select(p => new WeatherPlace(
                    string.Join(" ", new[] { Field(p, "country"), Field(p, "admin1"), Field(p, "name") }.Where(s => s.Length > 0).Distinct()),
                    p.GetProperty("latitude").GetDouble(), p.GetProperty("longitude").GetDouble())).ToArray();
        }
        if (places.Length == 0) return (null, $"‘{region}’ 지역을 찾지 못했어요. 시·군·구 이름이나 영문 도시 이름으로 다시 적어 줘.");
        if (places.Length > 1) return (null, "같은 이름의 지역이 여러 곳이에요. 시·도까지 붙여서 다시 물어봐.\n" +
            string.Join("\n", places.Take(6).Select(p => p.Name)) + "\n예: 경기도 광주 날씨");
        return (places[0], null);
    }

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private static void ClearWeatherPlace(SkillContext context)
    {
        context.Weather = null; context.WeatherDate = null; context.WeatherClock = null;
        context.LastWeather = true; // A follow-up still concerns weather, but cannot reuse the old city.
    }

    private async Task<string> HourlyForecast(WeatherPlace place, int hours, CancellationToken token, WeatherClockRange? range = null, SkillContext? context = null)
    {
        var data = await Fetch(FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude}&longitude={place.Longitude}&timezone=auto&forecast_days=3&timeformat=unixtime&hourly=temperature_2m,precipitation_probability,weather_code"), token);
        var now = Now;
        try
        {
            var offset = TimeSpan.FromSeconds(data.GetProperty("utc_offset_seconds").GetInt32());
            var timezone = Field(data, "timezone");
            DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone)
                ? TimeZoneInfo.ConvertTime(value, zone) : value.ToOffset(offset);
            var hourly = data.GetProperty("hourly");
            var times = hourly.GetProperty("time");
            var values = hourly.GetProperty("temperature_2m");
            var localDay = range?.SelectedDate?.ToDateTime(TimeOnly.MinValue) ?? Local(now).Date.AddDays(range?.DayOffset ?? 0);
            var start = range is null ? DateTime.MinValue : localDay.AddHours(range.StartHour);
            var end = range is null ? DateTime.MinValue : localDay.AddHours(range.EndHour);
            var alternative = range?.AlternativeHour is int alternativeHour ? localDay.AddHours(alternativeHour) : (DateTime?)null;
            var readings = times.EnumerateArray().Select((t, i) => (Time: DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()), Index: i))
                .Where(t => range is null ? t.Time > now && t.Time <= now.AddHours(hours) : alternative.HasValue ? Local(t.Time).DateTime == start || Local(t.Time).DateTime == alternative.Value : Local(t.Time).DateTime >= start && Local(t.Time).DateTime <= end).ToArray();
            int expectedCount = alternative.HasValue ? 2 : range is null ? hours : range.EndHour - range.StartHour + 1;
            if (readings.Length != expectedCount || !alternative.HasValue && readings.Zip(readings.Skip(1)).Any(p => p.Second.Time - p.First.Time != TimeSpan.FromHours(1)))
                throw new InvalidOperationException("Incomplete hourly forecast");
            double? OptionalNumber(string name, int index) => hourly.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > index && array[index].ValueKind == JsonValueKind.Number && array[index].TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
            string Condition(int index) => OptionalNumber("weather_code", index) switch {
                    0 => "맑음", 1 => "대체로 맑음", 2 => "구름 조금", 3 => "흐림",
                    45 or 48 => "안개", 51 or 53 or 55 => "이슬비", 56 or 57 => "얼어붙는 이슬비",
                    61 or 63 or 65 => "비", 66 or 67 => "얼어붙는 비", 71 or 73 or 75 => "눈", 77 => "싸락눈",
                    80 or 81 or 82 => "소나기", 85 or 86 => "소낙눈", 95 or 97 => "뇌우", 96 or 99 => "우박 동반 뇌우", _ => "" };
            string Conditions(int index)
            {
                string label = Condition(index);
                var probability = OptionalNumber("precipitation_probability", index);
                return (label.Length > 0 ? " · " + label : "") + (probability is >= 0 and <= 100 ? " · 강수확률 " + probability.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "");
            }
            string Temperature(int index) => values[index].ValueKind == JsonValueKind.Number && values[index].TryGetDouble(out var value)
                ? value.ToString("0.#", CultureInfo.InvariantCulture) + "°C" : "자료 없음";
            string Day(DateTime time) => (time.Date - Local(now).Date).Days switch {
                0 => "오늘", 1 => "내일", 2 => "모레", _ => time.ToString("M월 d일", CultureInfo.InvariantCulture) };
            string Hour(DateTime time) => time.Hour switch {
                0 => "자정", 12 => "낮 12시", < 12 => $"오전 {time.Hour}시", _ => $"오후 {time.Hour - 12}시" };
            if (context is not null)
            {
                context.WeatherDate = DateOnly.FromDateTime(range is null ? Local(readings[0].Time).DateTime : localDay);
                context.WeatherClock = range;
            }
            string answer;
            if (range is not null && start == end && !alternative.HasValue)
            {
                int index = readings[0].Index;
                string temperature = Temperature(index), condition = Condition(index);
                answer = $"{place.Name} {Day(start)} {Hour(start)}{(start.Hour == 0 ? "은" : "는")} " + (temperature == "자료 없음"
                    ? "기온 자료가 없어요." : temperature + "로 예상돼요" + (condition.Length > 0 ? $" ({condition})" : "") + ".");
                if (OptionalNumber("precipitation_probability", index) is double probability && probability is >= 0 and <= 100)
                    answer += "\n강수확률은 " + probability.ToString("0.#", CultureInfo.InvariantCulture) + "%야.";
            }
            else
            {
                var lines = readings.Select(t => {
                    var time = Local(t.Time).DateTime;
                    return (time.Date == Local(now).Date ? "" : Day(time) + " ") + Hour(time) + " · " + Temperature(t.Index) + Conditions(t.Index);
                });
                string label = alternative.HasValue ? "오전·오후 둘 다 봤어요." : range is null ? $"앞으로 {hours}시간 예보야." : "시간별 예보야.";
                answer = $"{place.Name} {label}\n" + string.Join("\n", lines);
            }
            return answer + (range is not null && start < Local(now).DateTime ? "\n지난 시간은 예보 기준이야." : "");
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or FormatException)
        { throw new ApiFailure(502, "forecast_hours", "요청한 시간의 예보 자료를 확인할 수 없어요. 잠시 뒤 다시 물어봐."); }
    }

    private async Task<string> Forecast(WeatherPlace place, bool tomorrow, CancellationToken token, SkillContext context)
    {
        var now = Now;
        var key = FormattableString.Invariant($"{place.Latitude},{place.Longitude}");
        if (!forecasts.TryGetValue(key, out var cached) || now - cached.Time >= TimeSpan.FromMinutes(10) ||
            now.ToOffset(TimeSpan.FromSeconds(cached.Data.GetProperty("utc_offset_seconds").GetInt32())).Date !=
            cached.Time.ToOffset(TimeSpan.FromSeconds(cached.Data.GetProperty("utc_offset_seconds").GetInt32())).Date)
        {
            var uri = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude}&longitude={place.Longitude}&timezone=auto&forecast_days=2&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max");
            cached = (now, await Fetch(uri, token));
            if (forecasts.Count >= 64) forecasts.Remove(forecasts.Keys.First());
            forecasts[key] = cached;
        }
        var daily = cached.Data.GetProperty("daily");
        int i = tomorrow ? 1 : 0;
        var expected = now.ToOffset(TimeSpan.FromSeconds(cached.Data.GetProperty("utc_offset_seconds").GetInt32())).AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (daily.GetProperty("time")[i].GetString() != expected)
            throw new ApiFailure(502, "forecast_date", "요청한 날짜의 예보를 확인할 수 없어요. 잠시 뒤 다시 확인해 줘.");
        string Reading(string name, string unit) => daily.GetProperty(name)[i].TryGetDouble(out var value)
            ? value.ToString("0.#", CultureInfo.InvariantCulture) + unit : "자료 없음";
        context.WeatherDate = DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        context.WeatherClock = null;
        return $"{place.Name} {(tomorrow ? "내일" : "오늘")}은 최저 {Reading("temperature_2m_min", "°C")}, 최고 {Reading("temperature_2m_max", "°C")}야.\n강수확률은 최대 {Reading("precipitation_probability_max", "%")}예요.";
    }

    private static string Field(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
    private async Task<JsonElement> Fetch(string uri, CancellationToken token)
    {
        try
        {
            using var response = await (client ?? Client).GetAsync(uri, token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            return json.RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is OperationCanceledException && !token.IsCancellationRequested)
        { throw new ApiFailure(503, "lookup_unavailable", "지금 자료를 받아오지 못했어요. 인터넷 연결을 확인하고 잠시 뒤 다시 해 봐."); }
    }
}
