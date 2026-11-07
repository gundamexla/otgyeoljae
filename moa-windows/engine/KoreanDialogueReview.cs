using System.Text.RegularExpressions;

namespace VoltiEngine;

// Adapted criteria: meaning/subject first, remove redundant framing, preserve requested voice.
// This is a bounded dialogue review, not the upstream long-form three-pass editorial pipeline.
public static class KoreanDialogueReview
{
    public const string Guidance = "말의 주어가 사용자와 모아 중 누구인지 구분하세요. 쉬운 한국어로 답하고 같은 뜻을 반복하지 마세요. 사용자가 지정한 말투와 내용을 유지하세요. 방법을 물으면 구체적인 방법을 답하세요.";
    public static bool HasDirectAdvice(string answer) => Regex.IsMatch(answer, @"(?:해\s*봐|해\s*보세요|해\s*보면|하세요|하자[.!?\s]|그만두면|(?:쉬|먹|하|가|마셔)(?:어|아|셔)?야\s*(?:해|돼|겠|죠|지요|지[.!?\s]|요)|(?:쉬|하|먹|마시)(?:시)?는\s*(?:게|것이)\s*(?:(?:제일|가장)\s*)?(?:좋|최고|중요)|(?:쉬어?|먹어?|마셔|산책해)\s*(?:봐|보면)|(?:쉬|드|먹|하|자)(?:시|셨)?(?:면|으면)\s*좋겠|(?:쉬|먹|하|자|시작하)(?:셔|여|아|어)?도\s*돼|지\s*말고)");
    public static int SentenceCount(string text) => Regex.Split(text.Trim(), @"(?<=[.!?。！？])(?:\s+|$)|[\r\n]+").Count(s => !string.IsNullOrWhiteSpace(s));
    public static string[] Issues(string question, string answer, ChatMessage[]? history = null)
    {
        if (MoaLanguageGuidance.RequestedLiteral(question) is { } literal)
            return answer.Trim() == literal ? [] : ["literal_changed"];
        var issues = new List<string>();
        var rules = ConversationRules.Read(history, question);
        if (MoaLanguageGuidance.IsDraftRequest(question) && !question.Contains("모아") && Regex.IsMatch(answer, @"모아(?:야|님)|(?:저는|제\s*이름은)\s*모아")) issues.Add("draft_role_confusion");
        if (rules.Tone == "casual" && Regex.IsMatch(answer, @"^(?:네[,!\s]*)?반말(?:이네|이야|로|을)")) issues.Add("style_acknowledgement");
        if (rules.NoQuestions && answer.IndexOfAny(['?', '？']) >= 0) issues.Add("no_questions");
        if (rules.NoAdvice && HasDirectAdvice(answer)) issues.Add("no_advice");
        if (rules.SentenceLimit is int max && SentenceCount(answer) > max) issues.Add("sentence_limit");
        bool fiction = Regex.IsMatch(question, @"소설|대본|역할극|예문|인용|(?:일기|이야기).*(?:써|만들)|그대로\s*(?:말해|읽어)");
        if (!fiction)
        {
            if (!MoaLanguageGuidance.IsDraftRequest(question) && Regex.IsMatch(answer, @"^안녕(?:하세요|해)?[,!\s]+모아(?:야|님)[.!\s]")) issues.Add("assistant_name_as_user");
            if (rules.Tone == "casual" && !Regex.IsMatch(answer, "[‘’“”\"]|```") &&
                Regex.IsMatch(answer, @"(?:요|습니다|입니다)(?=[.!?…]*(?:\s|$))")) issues.Add("requested_casual_tone");
            // Narrow defect detection, not a universal grammar checker. Never rewrite quotations.
            if (MoaLanguageGuidance.IsIdentityQuestion(question) && !Regex.IsMatch(answer, "[‘’“”\"]|```"))
            {
                if (Regex.IsMatch(answer, @"모아(?:야\s*해|야야|예요요|에요요)(?:[.!?\s]|$)")) issues.Add("broken_identity_ending");
                if (rules.Tone != "casual" && Regex.IsMatch(answer, @"(?:제\s*이름은|저는)\s*모아야(?:[.!?\s]|$)")) issues.Add("mixed_identity_tone");
            }
            if (Regex.IsMatch(question, @"(?:끝내|끝내지|끝내진|끝나지|다\s*하|다\s*하지|다\s*하진)\s*못|(?:다\s*)?못\s*(?:끝|했)") &&
                Regex.IsMatch(answer, @"다\s*(?:끝냈|했|끝내셨|하셨)|모두\s*(?:끝|완료)|전부\s*(?:끝|완료)") &&
                !Regex.IsMatch(answer, @"못|않|아직|미완")) issues.Add("reversed_fact");
            if (rules.Tone == "friendly" && Regex.IsMatch(answer, @"(?:하셨다|버티셨다|했습니다|하겠습니다|드립니다|하십시오)(?:[.!?\s]|$)")) issues.Add("stiff_tone");
            if (rules.Excluded is { Length: > 0 })
                foreach (var sentence in Regex.Split(answer, @"[.!?。！？\r\n]+"))
                    if (rules.Excluded.Any(x => sentence.Contains(x, StringComparison.Ordinal)) &&
                        Regex.IsMatch(sentence, @"추천|입어|입으면|입기|먹어|먹으면|좋아요|좋겠|챙겨") &&
                        !Regex.IsMatch(sentence, @"제외|빼고|말고|않|아니|대신")) issues.Add("excluded_recommendation");
            foreach (var sentence in Regex.Split(answer, @"[.!?。！？\r\n]+"))
                if (Regex.IsMatch(sentence, @"(?:저는|나는|저도|나도|제가|내가).{0,45}(?:먹었|마셨어|잤어|잤어요|다녀왔|여행했)") &&
                    !Regex.IsMatch(sentence, @"않|못|아니|없|수\s*없|라고|다는")) issues.Add("invented_experience");
        }
        if (Regex.IsMatch(question, @"(?:짧게|간단히|공감만)") && answer.Length > 350 && !fiction) issues.Add("unrequested_expansion");
        return issues.Distinct().ToArray();
    }
    public static string Repair(string[] issues) => string.Join(" ", issues.Distinct().Select(issue => issue switch
    {
        "no_questions" => "사용자가 질문을 원하지 않으니 질문 없이 반응하세요.",
        "no_advice" => "사용자는 공감만 원합니다. 무엇을 하라는 조언 없이 느꼈을 마음에 짧게 반응하세요.",
        "sentence_limit" => "지정된 문장 수 이내로 핵심만 답하세요.",
        "invented_experience" => "모아는 식사·수면·여행을 직접 하지 않는 소프트웨어입니다. 실제로 경험했다고 말하지 마세요.",
        "broken_identity_ending" => "자기소개 어미가 잘못됐습니다. 이름은 모아입니다. ‘저는 모아예요.’ 또는 요청한 반말이면 ‘나는 모아야.’처럼 완전한 문장으로 다시 답하세요.",
        "mixed_identity_tone" => "존댓말 주어와 반말 어미를 섞지 마세요. 해요체 자기소개는 ‘제 이름은 모아예요.’처럼 쓰세요.",
        "requested_casual_tone" => "질문의 내용에 바로 답하세요. 주어는 ‘나는’, 문장 끝은 ‘할 수 있어’, ‘도와줄게’처럼 쓰세요.",
        "literal_changed" => "원문을 바꿨습니다. 사용자의 콜론 뒤 문장을 글자 그대로 출력하세요. 말투를 고치거나 설명을 붙이지 마세요.",
        "draft_role_confusion" => "사용자를 대신해 다른 사람에게 보낼 문장입니다. 모아는 상대의 이름이 아닙니다. 요청에 없는 이름을 빼고 사용자의 입장에서 문장만 다시 쓰세요.",
        "style_acknowledgement" => "말투에 대한 안내는 빼고 사용자가 물은 내용에 바로 답하세요.",
        "assistant_name_as_user" => "모아는 당신의 이름이며 사용자를 부르는 호칭이 아닙니다. 인사나 호칭을 붙이지 말고 질문 내용에 바로 답하세요.",
        "unrequested_expansion" => "짧은 답을 원하니 반복과 안내를 줄이세요.",
        "stiff_tone" => "딱딱한 보고 말투 대신 자연스러운 해요체로 말하세요. 예: ‘확인했어요’, ‘많이 지쳤겠어요’.",
        "reversed_fact" => "사용자는 일을 다 끝내지 못했다고 했어요. 완료했다고 바꾸거나 사용자의 결정을 부정하지 마세요. 그 말 그대로 받아들여 반응하세요.",
        "excluded_recommendation" => "사용자가 빼 달라고 한 항목을 다시 추천하지 마세요. 다른 항목으로 바꾸세요.",
        "mixed_script" => "온전한 한국어로 다시 작성하세요. 필요한 영어 용어와 고유명사만 유지하세요.",
        "broken_unicode" => "깨진 문자 없이 온전한 한국어로 쓰세요.",
        "repetition" => "이전 답변을 되풀이하지 말고 최신 요청에 답하세요.",
        "user_echo" => "사용자의 말을 자신의 경험처럼 따라 하지 마세요.",
        _ => "최신 요청과 조건을 지키세요."
    }));
}
