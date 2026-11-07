using System.Text.RegularExpressions;

namespace VoltiEngine;

// Match a complete first-person need, not isolated words from quotes or history.
// The offer itself never launches a browser or submits any site data.
public static class SiteSuggestions
{
    private const string Self = @"(?:나|난|나는|내가|저|저는|제가)?";
    private const string Degree = @"(?:좀|조금|많이|너무|계속|자꾸){0,2}";
    private const string Ask = @"(?:줘(?:요)?|주세요|줄래(?:요)?|주실래요|줄수있어(?:요)?)";
    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool Health(string text)
    {
        const string time = @"(?:(?:오늘|요즘|지금)(?:은)?|(?:어제|오늘|아까)부터)?";
        const string body = @"(?:(?:내|제)?(?:몸|머리|배|목|허리|어깨|무릎|관절|다리|손목|치아)(?:이|가|도|는)?)?";
        const string pain = @"(?:아파(?:요)?|아프다|아픈데(?:요)?|아프네(?:요)?|아픕니다)";
        return Matches(text, Self + time + Self + body + Degree + pain) ||
            Matches(text, Self + time + Self + @"(?:몸|속)(?:이|가)?" + Degree + @"안좋아(?:요)?") ||
            Matches(text, Self + time + Self + @"두통(?:이|가)?" + Degree + @"(?:있어(?:요)?|있는데(?:요)?)");
    }

    private static bool Outfit(string text)
    {
        const string time = @"(?:(?:오늘|내일|이번주)(?:은)?(?:입을|입고갈)?)?";
        return Matches(text, Self + time + @"(?:뭐입지|뭘입을까|무슨옷입지|뭐입으면좋을까)(?:요)?") ||
            Matches(text, Self + time + @"(?:옷|의상|코디)(?:을|를)?" + Degree + @"(?:골라|추천해|정해|코디해|도와)" + Ask) ||
            Matches(text, Self + time + @"(?:옷|의상)(?:을|를)?(?:고르고싶어|미리입어보고싶어)(?:요)?") ||
            Matches(text, Self + time + @"(?:무슨옷을?입어야할지|뭘입어야할지)모르겠어(?:요)?");
    }

    private static bool Finance(string text)
    {
        const string time = @"(?:(?:이번달|이달|이번월|이번주|요즘|최근)(?:에|은|에는)?|최근에)?";
        return Matches(text, Self + time + Self + @"(?:돈|생활비|용돈|카드값)(?:을|를)?" + Degree + @"(?:썼어(?:요)?|써버렸어(?:요)?|쓴것같아(?:요)?|쓰는것같아(?:요)?)") ||
            Matches(text, Self + time + Self + @"(?:지출|소비|카드값)(?:이|가)?" + Degree + @"(?:많아(?:요)?|많았어(?:요)?|늘었어(?:요)?)") ||
            Matches(text, Self + time + Self + @"과소비(?:를)?(?:했어|한것같아)(?:요)?") ||
            Matches(text, Self + time + @"(?:돈관리(?:를)?하고싶어|가계부(?:를)?정리하고싶어|금융공부(?:를)?하고싶어|지출(?:을)?" + Degree + @"줄이고싶어|돈(?:을)?" + Degree + @"아끼고싶어)(?:요)?") ||
            Matches(text, Self + time + @"(?:예산|가계부|지출)(?:을|를)?(?:정리|관리)(?:를)?" + Degree + @"도와" + Ask);
    }

    public static NaturalToolRequest? Parse(string input, SkillContext context, SiteRegistry sites)
    {
        string text = Regex.Replace(NaturalToolLanguage.Address(input), @"\s+", "").TrimEnd('.', '!', '?', '。', '？');
        string? name = null, reply = null;
        if (text.Length > 500) return null;
        if (Health(text))
        {
            name = "병원";
            reply = "몸이 불편하시군요. 만들어 둔 ‘모아 동네의원’ 문진 사이트를 열어 드릴까요? 실제 의료진 연결이나 진단·예약이 아닌 안내형 데모예요.";
        }
        else if (Outfit(text))
        {
            name = "의상 착장";
            reply = "만들어 둔 의상 착장 사이트에서 옷을 살펴볼 수 있어요. 사이트를 열어 드릴까요?";
        }
        else if (Finance(text))
        {
            name = "금융";
            reply = "만들어 둔 가상금융 사이트를 열어 드릴까요? 금융 생활을 연습할 수 있는 사이트예요.";
        }
        else if (Matches(text, @"(?:내)?(?:옷장|의상자료)(?:을|를)?(?:보고싶어|찾아보고싶어)(?:요)?"))
        {
            name = "의상 자료실";
            reply = "옷을 찾아볼 수 있는 의상 자료실을 열어 드릴까요? 자료실의 의상이 모두 내 소유인 것은 아니며, 로그인이 필요할 수 있어요.";
        }
        else if (Matches(text, @"(?:매장|의상|옷걸이)?재고(?:를)?(?:확인하고싶어|보고싶어)(?:요)?"))
        {
            name = "재고 관리";
            reply = "THREAD 재고 관리 사이트를 열어 드릴까요? 매장·POS·옷걸이 시연을 확인할 수 있고 로그인이 필요할 수 있어요.";
        }
        else if (Matches(text, @"(?:3D|3d|삼디)(?:로)?(?:착장을?보고싶어|입어보고싶어)(?:요)?"))
        {
            name = "3D 착장";
            reply = "3D 착장 스튜디오를 열어 드릴까요? 사진 착장 사이트와 별도 버전이에요.";
        }
        else if (Matches(text, @"(?:아이디어시나리오|제작자료|해커톤자료)(?:를)?보고싶어(?:요)?"))
        {
            name = "프로젝트 자료실";
            reply = "아이디어와 제작 기록이 모인 프로젝트 자료실을 열어 드릴까요?";
        }
        if (name is null) return null;
        var found = sites.Find(name);
        if (found.Length != 1) return null;
        context.PendingSiteOffer = new("site", found[0].Name, found[0].Url);
        return new(Reply: reply + "\n‘응 열어줘’면 열고, ‘아니 지금은 됐어’면 취소해요. ‘착장으로 바꿔줘’처럼 다른 사이트를 골라도 돼요.");
    }
}
