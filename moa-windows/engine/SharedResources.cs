namespace VoltiEngine;
public static class SharedResources
{
    public const string MoaRoot = @"C:\Users\LG\Desktop\ai 프로젝트 모음\OBA.RUN 2nd 해커톤\moa";
    public const string Model = @"C:\Users\LG\Desktop\ai 프로젝트 모음\OBA.RUN 2nd 해커톤\volti-pet\desktop\models\Qwen3.5-4B-Q4_K_M.gguf";
    public const string Runtime = @"C:\Users\LG\Desktop\ai 프로젝트 모음\OBA.RUN 2nd 해커톤\volti-pet\desktop\runtime\llama-server.exe";
    public static string Exact(string value, string allowed)
    {
        if (!Path.IsPathFullyQualified(value) || !Path.GetFullPath(value).Equals(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("승인된 공유 모델·실행기 파일 경로만 사용할 수 있어요.");
        return allowed;
    }
    public static string PrivatePath(string value)
    {
        string path = Path.GetFullPath(value);
        if (!path.StartsWith(MoaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("모아 데이터는 모아 폴더 안에만 저장할 수 있어요.");
        for (string? part = path; part != null && part.Length >= MoaRoot.Length; part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("모아 데이터 경로에는 폴더 연결을 사용할 수 없어요.");
        return path;
    }
}
