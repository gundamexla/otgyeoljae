using System.Text.RegularExpressions;

namespace VoltiEngine;

// A narrow display normalization of familiar sentence endings, never a semantic rewrite.
public static class KoreanTone
{
    public static string Apply(string question, string answer, ConversationRules rules)
    {
        if (rules.Tone != "friendly" || Regex.IsMatch(question, "소설|대본|예문|인용|그대로|번역|교정|반말로") ||
            Regex.IsMatch(answer, "[‘’“”\"]|```")) return answer;
        return Regex.Replace(answer, @"(?<stem>[가-힣]{2,})(?<ending>겠어|었어|았어|했어|있어|없어|같아|않아|괜찮아|피곤해|힘들어|거야)(?=[.!?…]*(?:\s|$))", match =>
            match.Groups["stem"].Value + (match.Groups["ending"].Value == "거야" ? "거예요" : match.Groups["ending"].Value + "요"));
    }
}
