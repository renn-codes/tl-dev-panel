using System.Security.Cryptography;
using System.Text;

namespace TeamLigma.Panel.Server.Auth;

/// <summary>PBKDF2 password hashing.</summary>
public static class Passwords
{
    const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var p = stored.Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2-sha256") return false;
            var iter = int.Parse(p[1]);
            var salt = Convert.FromBase64String(p[2]);
            var expected = Convert.FromBase64String(p[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    /// <summary>Returns an error message, or null when the password is acceptable.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 10) return "Passwords must be at least 10 characters.";
        if (password.Length > 256) return "Passwords must be at most 256 characters.";
        return null;
    }
}

/// <summary>RFC 6238 time-based one-time passwords (SHA-1, 6 digits, 30 s) as used by authenticator apps.</summary>
public static class Totp
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public const int Step = 30;

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static long CurrentStep(DateTimeOffset? now = null) => (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / Step;

    public static string Code(string base32Secret, long step)
    {
        var key = Base32Decode(base32Secret);
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var bin = (hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    /// <summary>Verifies a code within ±1 step; returns the matched step (for replay protection) or null.</summary>
    public static long? Verify(string base32Secret, string code, long lastUsedStep)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6) return null;
        var now = CurrentStep();
        for (var s = now - 1; s <= now + 1; s++)
        {
            if (s <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(base32Secret, s)), Encoding.ASCII.GetBytes(code)))
                return s;
        }
        return null;
    }

    public static string Uri(string issuer, string account, string secret)
        => $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
           $"?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period={Step}";

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = buffer << 8 | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string s)
    {
        s = s.TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid base32 secret");
            buffer = buffer << 5 | v; bits += 5;
            if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return output.ToArray();
    }
}

public static class RecoveryCodes
{
    const string Chars = "abcdefghjkmnpqrstuvwxyz23456789";

    public static List<string> Generate(int count = 8)
        => Enumerable.Range(0, count).Select(_ =>
        {
            var c = new char[8];
            for (var i = 0; i < 8; i++) c[i] = Chars[RandomNumberGenerator.GetInt32(Chars.Length)];
            return new string(c, 0, 4) + "-" + new string(c, 4, 4);
        }).ToList();

    public static string Normalize(string code) => new string((code ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));
}
