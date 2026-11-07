using System.Text.RegularExpressions;
namespace VoltiEngine;

public static class MoaLanguageGuidance
{
    // Tone is already supplied through ConversationRules. Keep the original in history,
    // but do not invite the small model to answer the leading style instruction itself.
    public static string ContentRequest(string question)
    {
        var match = Regex.Match(question, @"^(?:반말|존댓말)(?:로|만)\s*(?:말해|답해)\s*줘[.!]\s*(?<content>\S[\s\S]*)$");
        return match.Success ? match.Groups["content"].Value : question;
    }
    public static bool IsDraftRequest(string question) => Regex.IsMatch(question, @"(?:문장|메시지|문자|편지).{0,30}(?:써|작성|만들)");
    public static string? RequestedLiteral(string question)
    {
        var match = Regex.Match(question, @"^(?:다음\s*)?문장(?:을)?\s*그대로\s*(?:적어|써|말해|읽어)\s*줘\s*[:：]\s*(?<text>.+)$", RegexOptions.Singleline);
        return match.Success ? match.Groups["text"].Value.Trim() : null;
    }
    public static bool IsIdentityQuestion(string question) => !Regex.IsMatch(question, "인용|그대로|번역|교정|[‘’“”\"]") &&
        Regex.IsMatch(question, @"(?:^(?:안녕[!,.\s]*)?(?:(?:네|너의|니|당신의)\s*이름|너(?:는)?\s*(?:누구|정체))|^이름(?:이|은)?\s*(?:뭐|무엇))");
    public static string For(string question, ConversationRules rules)
    {
        if (RequestedLiteral(question) is not null) return "\n이번 요청은 원문을 그대로 쓰는 것입니다. 앞의 말투 지시보다 원문 보존이 우선입니다. 콜론 뒤의 문장만 한 글자도 바꾸지 않고 그대로 출력하세요. 어미에 ‘요’를 붙이거나 설명·따옴표를 덧붙이지 마세요.";
        if (IsIdentityQuestion(question))
            return rules.Tone == "casual" ? "\n이름을 묻는 질문입니다. 이름은 모아입니다. 자연스러운 자기소개 예: 나는 모아야." :
                "\n이름을 묻는 질문입니다. 이름은 모아입니다. 자연스러운 자기소개 예: 저는 모아예요.";
        if (Regex.IsMatch(question, @"^(?:안녕|반가워|좋은\s*아침)")) return "\n인사에는 짧게 답하되 함께 한 질문이나 요청이 있으면 그 내용을 우선 처리하세요. 이름을 묻지 않았다면 자기소개는 하지 마세요.";
        if (IsDraftRequest(question))
        {
            var guidance = "\n사용자가 상대에게 바로 보낼 수 있는 짧은 문장만 작성하세요. 화자는 사용자입니다. 누가 무엇을 하는지와 시제를 정확히 쓰세요. 사용자가 앞으로 할 일을 이미 한 일로 바꾸지 마세요. 요청에 없는 이름·호칭·상황·다짐을 넣지 마세요. 같은 뜻의 사과는 한 번만 쓰세요. 설명 없이 요청한 문장만 출력하세요.";
            if (Regex.IsMatch(question, "늦") && Regex.IsMatch(question, "사과|미안"))
                guidance += rules.Tone == "casual" ? "\n다른 상황의 작성 예: ‘조금 늦을 것 같아. 기다리게 해서 미안해.’ 실제 답변에는 사용자가 준 시간 정보를 반영하세요." :
                    "\n다른 상황의 작성 예: ‘조금 늦을 것 같아요. 기다리게 해서 죄송해요.’ 실제 답변에는 사용자가 준 시간 정보를 반영하세요.";
            return guidance;
        }
        return "";
    }
}
