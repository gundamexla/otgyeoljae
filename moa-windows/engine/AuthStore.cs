using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoltiEngine;

public sealed record EnginePrincipal(string Id, bool Native, string? Origin);
public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt);
public sealed record Connection(string Id, string Origin, string TokenHash, DateTimeOffset CreatedAt);

public sealed class AuthStore
{
    private readonly object gate = new();
    private readonly string nativeHash;
    private readonly string connectionsPath;
    private readonly List<Connection> connections;
    private readonly Dictionary<string, PendingPair> pending = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> pairAttempts = new();
    private sealed record PendingPair(string Origin, DateTimeOffset ExpiresAt);

    public AuthStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, "native-token.txt");
        if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, NewToken(), new UTF8Encoding(false));
        var token = File.ReadAllText(tokenPath).Trim();
        if (token.Length < 32 || token.Length > 200) throw new InvalidOperationException("엔진 연결 정보 파일이 올바르지 않아요.");
        nativeHash = Hash(token);
        connectionsPath = Path.Combine(dataDirectory, "connections.json");
        connections = File.Exists(connectionsPath)
            ? JsonSerializer.Deserialize<List<Connection>>(File.ReadAllText(connectionsPath), JsonDefaults.Options)
                ?? throw new InvalidOperationException("사이트 연결 정보를 읽지 못했어요.")
            : new();
        if (connections.Count > 64 || connections.Any(c => !IsAllowedOrigin(c.Origin) || c.TokenHash.Length != 64))
            throw new InvalidOperationException("사이트 연결 정보가 올바르지 않아요.");
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool EqualHash(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    public static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origin.Length > 512 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.GetLeftPart(UriPartial.Authority) != origin) return false;
        return uri.Scheme == "https" || origin == "http://127.0.0.1:4175";
    }

    public bool HasOrigin(string origin)
    {
        lock (gate) return connections.Any(c => c.Origin == origin);
    }

    public EnginePrincipal? Authenticate(string? authorization, string? origin)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 256) return null;
        var candidate = Hash(authorization[7..]);
        if (origin is null && EqualHash(candidate, nativeHash)) return new("native", true, null);
        // A native token is intentionally unusable by browser requests, even
        // from an already approved origin. Browser grants have their own owner.
        if (origin is null || !IsAllowedOrigin(origin)) return null;
        lock (gate)
        {
            var match = connections.FirstOrDefault(c => c.Origin == origin && EqualHash(candidate, c.TokenHash));
            return match is null ? null : new(match.Id, false, match.Origin);
        }
    }

    public PairingCode CreatePairingCode(string origin)
    {
        if (!IsAllowedOrigin(origin)) throw new ApiFailure(400, "invalid_origin", "HTTPS 사이트의 주소만 입력해 주세요. 로컬 검수 주소는 http://127.0.0.1:4175예요.");
        lock (gate)
        {
            RemoveExpired();
            if (pending.Count >= 32) throw new ApiFailure(429, "too_many_codes", "잠시 후 연결 코드를 다시 만들어 주세요.");
            if (connections.Count >= 64) throw new ApiFailure(429, "too_many_connections", "사용하지 않는 사이트 연결을 먼저 해제해 주세요.");
            var code = NewToken();
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            pending.Add(Hash(code), new(origin, expires));
            return new(code, expires);
        }
    }

    public string Pair(string? origin, string? code)
    {
        if (!IsAllowedOrigin(origin)) throw new ApiFailure(403, "origin_denied", "이 사이트에서는 엔진에 연결할 수 없어요.");
        lock (gate)
        {
            RemoveExpired();
            // Bound both per-origin attempts and the number of remembered origins.
            if (!pairAttempts.TryGetValue(origin!, out var attempts))
            {
                if (pairAttempts.Count >= 128) throw new ApiFailure(429, "pair_rate_limit", "연결 시도가 많아요. 잠시 후 다시 시도해 주세요.");
                attempts = new(); pairAttempts.Add(origin!, attempts);
            }
            while (attempts.TryPeek(out var time) && time < DateTimeOffset.UtcNow.AddMinutes(-1)) attempts.Dequeue();
            if (attempts.Count >= 10) throw new ApiFailure(429, "pair_rate_limit", "연결 코드를 확인하고 잠시 후 다시 시도해 주세요.");
            attempts.Enqueue(DateTimeOffset.UtcNow);
            if (code is null || code.Length != 64 || !pending.TryGetValue(Hash(code), out var match) || match.Origin != origin)
                throw new ApiFailure(403, "invalid_pairing_code", "연결 코드가 틀렸거나 만료됐어요. 이 사이트용 코드를 다시 만들어 주세요.");
            if (connections.Count >= 64) throw new ApiFailure(429, "too_many_connections", "사용하지 않는 사이트 연결을 먼저 해제해 주세요.");
            var token = NewToken();
            var connection = new Connection(Guid.NewGuid().ToString("N"), origin!, Hash(token), DateTimeOffset.UtcNow);
            connections.Add(connection);
            try { Save(); }
            catch { connections.Remove(connection); throw; }
            pending.Remove(Hash(code));
            return token;
        }
    }

    public object ListConnections()
    {
        lock (gate) return new { connections = connections.Select(c => new { c.Id, c.Origin, c.CreatedAt }).ToArray() };
    }

    public bool Revoke(string id)
    {
        lock (gate)
        {
            var found = connections.FirstOrDefault(c => c.Id == id);
            if (found is null) return false;
            connections.Remove(found);
            try { Save(); }
            catch { connections.Add(found); throw; }
            return true;
        }
    }

    public bool IsActive(EnginePrincipal principal)
    {
        if (principal.Native) return true;
        lock (gate) return connections.Any(c => c.Id == principal.Id);
    }

    private void RemoveExpired()
    {
        foreach (var key in pending.Where(p => p.Value.ExpiresAt < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) pending.Remove(key);
        foreach (var key in pairAttempts.Where(p => p.Value.Count == 0 || p.Value.Last() < DateTimeOffset.UtcNow.AddMinutes(-1)).Select(p => p.Key).ToArray()) pairAttempts.Remove(key);
    }

    private void Save()
    {
        var temporary = connectionsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(connections, JsonDefaults.Options), new UTF8Encoding(false));
            File.Move(temporary, connectionsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
