using System.Text;
using Windows.UI;

namespace TeamLigma.Panel.Controls;

public sealed class TermSeg
{
    public string Text = "";
    public Color? Fg;
    public bool Bold;
}

public sealed class TermLine
{
    public List<TermSeg> Segs { get; } = new();
}

/// <summary>Stateful ANSI/VT parser producing coloured line segments (SGR colours only; other sequences are dropped).</summary>
public sealed class AnsiParser
{
    public struct State { public Color? Fg; public bool Bold; }
    public State Current;

    static readonly Color[] Base =
    {
        C(0x5A,0x58,0x52), C(0xE5,0x53,0x4B), C(0x6F,0xBF,0x73), C(0xE0,0xB3,0x41),
        C(0x6C,0xA0,0xDC), C(0xC6,0x78,0xDD), C(0x56,0xB6,0xC2), C(0xD7,0xD5,0xCC),
        C(0x85,0x83,0x7A), C(0xFF,0x6B,0x63), C(0x8F,0xD6,0x94), C(0xF2,0xCC,0x60),
        C(0x8C,0xB8,0xEE), C(0xD9,0x9A,0xE8), C(0x7F,0xD0,0xDA), C(0xFF,0xFF,0xFF),
    };
    public static readonly Color Red = Base[1], Green = Base[2], Yellow = Base[3], Cyan = Base[6], Muted = Base[8];
    public static readonly Color Accent = C(0xE0, 0x7A, 0x3F);

    static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    static Color Xterm256(int n)
    {
        if (n < 16) return Base[n];
        if (n < 232)
        {
            n -= 16;
            int r = n / 36, g = n / 6 % 6, b = n % 6;
            byte V(int x) => (byte)(x == 0 ? 0 : 55 + x * 40);
            return C(V(r), V(g), V(b));
        }
        var l = (byte)(8 + (n - 232) * 10);
        return C(l, l, l);
    }

    public TermLine ParseLine(string raw)
    {
        var line = new TermLine();
        if (raw.EndsWith('\r')) raw = raw.TrimEnd('\r');
        var sb = new StringBuilder();
        int col = 0;
        void Flush()
        {
            if (sb.Length == 0) return;
            line.Segs.Add(new TermSeg { Text = sb.ToString(), Fg = Current.Fg, Bold = Current.Bold });
            sb.Clear();
        }
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '\u001b')
            {
                if (i + 1 >= raw.Length) break;
                var next = raw[i + 1];
                if (next == '[')
                {
                    var j = i + 2;
                    while (j < raw.Length && (raw[j] < 0x40 || raw[j] > 0x7E)) j++;
                    if (j >= raw.Length) { i = raw.Length; break; }
                    if (raw[j] == 'm') { Flush(); Sgr(raw.Substring(i + 2, j - i - 2)); }
                    else if (raw[j] == 'K' || raw[j] == 'J') { /* erase: ignore */ }
                    i = j;
                }
                else if (next == ']')
                {
                    var j = i + 2;
                    while (j < raw.Length && raw[j] != '\a' && !(raw[j] == '\u001b' && j + 1 < raw.Length && raw[j + 1] == '\\')) j++;
                    i = (j < raw.Length && raw[j] == '\u001b') ? j + 1 : j;
                }
                else if (next is '(' or ')' or '#' or '%') i += 2;
                else i += 1;
                continue;
            }
            if (ch == '\r') { Flush(); line.Segs.Clear(); col = 0; continue; }
            if (ch == '\b') { if (sb.Length > 0) sb.Length--; col = Math.Max(0, col - 1); continue; }
            if (ch == '\t') { var n = 8 - col % 8; sb.Append(' ', n); col += n; continue; }
            if (ch < ' ' || ch == '\u007f') continue;
            sb.Append(ch);
            col++;
        }
        Flush();
        Highlight(line);
        return line;
    }

    void Sgr(string p)
    {
        var parts = p.Length == 0 ? new[] { "0" } : p.Split(';', ':');
        for (var k = 0; k < parts.Length; k++)
        {
            if (!int.TryParse(parts[k], out var c)) c = 0;
            switch (c)
            {
                case 0: Current = default; break;
                case 1: Current.Bold = true; break;
                case 22: Current.Bold = false; break;
                case >= 30 and <= 37: Current.Fg = Base[c - 30 + (Current.Bold ? 8 : 0)]; break;
                case >= 90 and <= 97: Current.Fg = Base[c - 90 + 8]; break;
                case 39: Current.Fg = null; break;
                case 38 or 48:
                    if (k + 1 < parts.Length && parts[k + 1] == "5" && k + 2 < parts.Length)
                    {
                        if (c == 38 && int.TryParse(parts[k + 2], out var n)) Current.Fg = Xterm256(Math.Clamp(n, 0, 255));
                        k += 2;
                    }
                    else if (k + 1 < parts.Length && parts[k + 1] == "2" && k + 4 < parts.Length)
                    {
                        if (c == 38 && byte.TryParse(parts[k + 2], out var r) && byte.TryParse(parts[k + 3], out var g) && byte.TryParse(parts[k + 4], out var b))
                            Current.Fg = C(r, g, b);
                        k += 4;
                    }
                    break;
            }
        }
    }

    /// <summary>Adds colour to plain (uncoloured) output: build steps, errors, warnings, SQL results.</summary>
    static void Highlight(TermLine line)
    {
        if (line.Segs.Count == 0 || line.Segs.Any(s => s.Fg != null)) return;
        var text = string.Concat(line.Segs.Select(s => s.Text));
        var t = text.TrimStart();
        Color? color = null;
        if (t.StartsWith("FAILED:") || t.StartsWith("ERROR") || t.Contains(" error:") || t.StartsWith("error:") || t.StartsWith("ninja: build stopped") || t.Contains("CMake Error"))
            color = Red;
        else if (t.Contains("warning:") || t.StartsWith("Warning") || t.Contains("CMake Warning"))
            color = Yellow;
        else if (t.StartsWith("=="))
            color = Accent;
        else if (t.StartsWith("Query OK") || t.Contains(" rows in set") || t.Contains(" row in set") || t.StartsWith("-- Installing") || t.StartsWith("-- Build files have been written") || t.StartsWith("-- Generating done"))
            color = Green;
        else if (t.StartsWith("-- Up-to-date") || t.StartsWith("-- Set non-toolchain"))
            color = Muted;
        else if (t.StartsWith("+-") || t.StartsWith("|"))
            color = Cyan;
        if (color != null)
        {
            foreach (var s in line.Segs) s.Fg = color;
            return;
        }
        // [12/1187] Building CXX object ...  /  mysql> prompt
        var prefixLen = 0;
        if (t.StartsWith('[') && t.IndexOf(']') is var close and > 1 && t[1..close].Contains('/')) prefixLen = text.IndexOf(']') + 1;
        else if (text.StartsWith("mysql>")) prefixLen = 6;
        else if (text.StartsWith("AC>")) prefixLen = 3;
        if (prefixLen > 0)
        {
            line.Segs.Clear();
            line.Segs.Add(new TermSeg { Text = text[..prefixLen], Fg = Accent, Bold = true });
            line.Segs.Add(new TermSeg { Text = text[prefixLen..] });
        }
    }
}
