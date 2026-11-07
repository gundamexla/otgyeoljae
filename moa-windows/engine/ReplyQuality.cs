using System.Text;
using System.Text.RegularExpressions;

namespace VoltiEngine;

// A script-mixing check, not a grammar checker or a factuality guarantee.
public static class ReplyQuality
{
    private static string Normalize(string text) => Regex.Replace(Regex.Replace(text.Trim(), @"^(?:그런데|그러면|그럼|응|그래)(?:\s+|[,!]+\s*)", ""), @"[^\p{L}\p{N}]", "");
    private static bool RequestsRestatement(string question) => Regex.IsMatch(question,
        @"그대로|똑같이|다시\s*(?:읽어|말해|써\s*줘|표현)|한\s*번\s*더\s*말해|(?:그걸|그거|방금\s*(?:말|답변)|이전\s*답변).{0,30}(?:정리|요약|짧게|다시)|내가\s*(?:뭐라고|뭐라|어떻게)\s*말|(?:한|두|세)\s*문장(?:으로|만)|더\s*짧게|(?:이모지|줄임말|영어|한자|존댓말|반말).{0,25}(?:빼|말고|없이|바꿔)");
    public static bool HasRepeatedLines(string answer) => answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => Regex.Replace(line, @"^\d+[.)]\s*", ""))
        .Where(line => line.Length >= 15).GroupBy(line => line).Any(group => group.Count() >= 2);
    public static bool RepeatsPreviousReply(string question, string answer, ChatMessage[] history)
    {
        if (RequestsRestatement(question)) return false;
        string value = Normalize(answer);
        var previous = history.LastOrDefault(m => m.Role == "assistant");
        return value.Length >= 10 && previous is not null && value == Normalize(previous.Text);
    }
    public static bool EchoesUserText(string question, string answer, ChatMessage[] history)
    {
        if (RequestsRestatement(question) || Regex.IsMatch(question, @"예문|소설|대본|역할극|번역|교정|따라\s*해")) return false;
        var value = Normalize(answer);
        if (value.Length < 12) return false;
        if (history.Where(m => m.Role == "user").TakeLast(4).Append(new("user", question)).Any(m => value == Normalize(m.Text))) return true;
        // Korean drops pronouns. Near-verbatim first-person endings can accidentally make the
        // user's experience the assistant's own; genuine reflective endings remain allowed.
        if (Regex.IsMatch(answer, "구나|군요|네요|겠|느꼈|들렸|말했|말한|같아요|같아|다니") || !Regex.IsMatch(answer.Trim(), @"(?:했어|했어요|었어|았어|어왔어|하려고)[.!]*$")) return false;
        var input = Normalize(question);
        if (input.Length < 12 || value.Length > 350) return false;
        // Ordered character overlap tolerates Korean particle and inflection changes.
        var row = new int[value.Length + 1];
        foreach (char c in input)
        {
            int previous = 0;
            for (int j = 1; j <= value.Length; j++)
            {
                int old = row[j]; row[j] = c == value[j - 1] ? previous + 1 : Math.Max(row[j], row[j - 1]); previous = old;
            }
        }
        return 2.0 * row[value.Length] / (input.Length + value.Length) >= 0.6;
    }
    public static bool HasBrokenUnicode(string question, string answer) => answer.Contains('\uFFFD') && !question.Contains('\uFFFD');
    public static bool WantsNoQuestions(string question, ChatMessage[]? history = null)
    {
        if (history?.Any(m => m.Role == "system" && m.Text.StartsWith(ConversationRules.Marker + "\n")) == true)
            return ConversationRules.Read(history, question).NoQuestions;
        foreach (var text in (history ?? []).Where(m => m.Role == "user").TakeLast(4).Select(m => m.Text).Append(question).Reverse())
        {
            if (Regex.IsMatch(text, @"(?:뜻은|말은)\s*아니|질문해도\s*(?:돼|괜찮)|질문(?:을|도)?\s*(?:하나|한\s*개)?\s*해\s*줘|물어봐도\s*돼")) return false;
            if (Regex.IsMatch(text, @"질문(?:은|도)?\s*(?:없이|말고)|질문\s*하지\s*(?:마|말아)|공감만")) return true;
        }
        return false;
    }
    public static bool IgnoresNoQuestionRequest(string question, string answer, ChatMessage[]? history = null) =>
        WantsNoQuestions(question, history) && answer.IndexOfAny(['?', '？']) >= 0;
    private static bool HasCjk(string text) => text.EnumerateRunes().Any(r =>
        r.Value is >= 0x3040 and <= 0x30ff or >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af);

    public static bool HasUnexpectedCjk(string question, string answer)
    {
        if (!HasCjk(answer)) return false;
        if (Regex.IsMatch(question, @"(?:중국어|일본어|한자).{0,20}(?:쓰지|섞지|빼|없이|말고|금지)|(?:한국어|한글)(?:로)?만")) return true;
        // Preserve quoted source text and explicit translation/language requests.
        // English identifiers, code, CPU/RAM and product names are never stripped.
        if (HasCjk(question) || Regex.IsMatch(question, "중국어|일본어|한자|원문|Chinese|Japanese", RegexOptions.IgnoreCase)) return false;
        return true;
    }

    public static bool HasUnexpectedCyrillic(string question, string answer)
    {
        bool ContainsCyrillic(string text) => text.EnumerateRunes().Any(r => r.Value is >= 0x0400 and <= 0x052f or >= 0x2de0 and <= 0x2dff or >= 0xa640 and <= 0xa69f);
        if (!ContainsCyrillic(answer)) return false;
        if (Regex.IsMatch(question, @"(?:한국어|한글)(?:로)?만")) return true;
        return !ContainsCyrillic(question) && !Regex.IsMatch(question, "러시아어|키릴|우크라이나어|불가리아어|세르비아어|Russian|Cyrillic|Ukrainian|Bulgarian|Serbian", RegexOptions.IgnoreCase);
    }

    public static async Task<string> GenerateAsync(string question,
        Func<int, CancellationToken, IAsyncEnumerable<string>> generate, CancellationToken token, ChatMessage[]? history = null, Action<string[]>? rejected = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var answer = new StringBuilder();
            try
            {
                await foreach (var chunk in generate(attempt, token))
                {
                    token.ThrowIfCancellationRequested();
                    answer.Append(chunk);
                    if (answer.Length > 12000) throw new ApiFailure(502, "reply_too_long", "답변이 너무 길어 생성을 멈췄어요.");
                }
            }
            catch (ApiFailure e) when (e.Code == "reply_truncated" && attempt == 0) { continue; }
            token.ThrowIfCancellationRequested();
            var result = answer.ToString().Trim();
            if (result.Length == 0) throw new ApiFailure(502, "empty_reply", "모델이 빈 답변을 보냈어요.");
            var issues = KoreanDialogueReview.Issues(question, result, history).ToList();
            if (HasUnexpectedCjk(question, result) || HasUnexpectedCyrillic(question, result)) issues.Add("mixed_script");
            if (HasBrokenUnicode(question, result)) issues.Add("broken_unicode");
            if (HasRepeatedLines(result) || RepeatsPreviousReply(question, result, history ?? [])) issues.Add("repetition");
            if (EchoesUserText(question, result, history ?? [])) issues.Add("user_echo");
            if (IgnoresNoQuestionRequest(question, result, history)) issues.Add("no_questions");
            if (issues.Count == 0) return result;
            rejected?.Invoke(issues.Distinct().ToArray());
        }
        throw new ApiFailure(502, "reply_quality_failed", "답변이 말투·반복 검사에 걸려 다시 작성했지만 해결하지 못했어요. 이 답변은 대화 기록에 저장하지 않았어요.");
    }
}
