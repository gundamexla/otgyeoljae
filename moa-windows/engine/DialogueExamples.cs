using System.Text.RegularExpressions;

namespace VoltiEngine;

// Small, curated in-context examples. These are guidance, not a trained model or canned replies.
public static class DialogueExamples
{
    public static string For(string input, ConversationRules rules)
    {
        var guidance = "\n사용자가 실제로 말한 사실과 아직 하려는 일을 구분하세요. 끝내지 못한 일을 끝냈다고 바꾸지 마세요.";
        // The voice comes only from ConversationRules; do not mix casual examples into polite replies.
        if (Regex.IsMatch(input, "추천|방법|어떻게|뭐부터")) guidance += "\n방법을 물었으니 현재 대상에 맞는 구체적인 방법을 먼저 알려 주세요. 사용자 대신 선택을 다시 묻는 답변만 하지 마세요.";
        if (rules.NoAdvice) guidance += "\n현재는 공감만 해야 합니다. 쉬기·산책·식사 같은 해결책이나 ‘해야 해’ 같은 권유를 덧붙이지 마세요.";
        return guidance;
    }
}
