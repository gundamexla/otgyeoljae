using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoltiEngine;

public sealed record WeatherPlace(string Name, double Latitude, double Longitude);

public static class KoreanPlaces
{
    private sealed record Region(string Name, double Latitude, double Longitude, string[] Aliases, string[] Parents);
    private static readonly Region[] Regions = Load();
    private static Region[] Load()
    {
        using var stream = typeof(KoreanPlaces).Assembly.GetManifestResourceStream("VoltiEngine.KoreanPlaces.json")
            ?? throw new InvalidOperationException("국내 지역 자료가 없어요.");
        return JsonSerializer.Deserialize<Region[]>(stream) ?? [];
    }
    private static string Normalize(string word)
    {
        word = word switch { "충남" => "충청남도", "충북" => "충청북도", "경남" => "경상남도", "경북" => "경상북도", "전남" => "전라남도", "전북" => "전북특별자치도", _ => word };
        // Administrative suffixes are optional, as in 안성 / 안성시.
        return Regex.Replace(word, "(특별자치도|특별자치시|특별시|광역시|자치시|자치도|도|시|군|구|읍|면|동)$", "");
    }
    public static WeatherPlace[] Find(string query)
    {
        var words = query.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var parts = words.Select(Normalize).ToArray();
        if (parts.Length == 0) return [];
        var matches = Regions.Where(r =>
        {
            var names = r.Aliases.Select(Normalize).ToArray();
            var hierarchy = r.Parents.Select(Normalize).Concat(names).ToHashSet();
            return parts.All(hierarchy.Contains) && parts.Any(names.Contains);
        }).ToArray();
        // Prefer an explicitly listed name over a shortened 읍/면/동 with the same stem.
        var exact = matches.Where(r => words.Any(r.Aliases.Contains)).ToArray();
        return (exact.Length > 0 ? exact : matches).Select(r => new WeatherPlace(r.Name, r.Latitude, r.Longitude)).ToArray();
    }
}
