using System.Text;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Server;

public static class FileTail
{
    const int MaxChunk = 512 * 1024;
    const int InitialTail = 384 * 1024;

    /// <summary>
    /// Reads a file from <paramref name="offset"/>. Offset -1 means "tail": start near the end,
    /// aligned to a line boundary. When the file shrank (truncated/recreated) the chunk is a reset.
    /// </summary>
    public static LogChunk Read(string path, long offset, string key, string title, bool running, bool full = false)
    {
        var chunk = new LogChunk { Key = key, Title = title, Running = running };
        if (!File.Exists(path)) { chunk.Offset = 0; chunk.Reset = offset != 0; return chunk; }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        if (offset < 0 || offset > len)
        {
            chunk.Reset = true;
            offset = full ? 0 : Math.Max(0, len - InitialTail);
        }
        var max = full ? int.MaxValue : MaxChunk;
        var count = (int)Math.Min(len - offset, max);
        var buffer = new byte[count];
        fs.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var n = fs.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        // Hand out everything (prompts have no trailing newline) but never split a UTF-8 sequence.
        var end = read;
        var back = 0;
        while (back < 4 && end - back - 1 >= 0 && (buffer[end - back - 1] & 0xC0) == 0x80) back++;
        if (end - back - 1 >= 0)
        {
            var lead = buffer[end - back - 1];
            var need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
            if (need > back + 1) end = end - back - 1;
        }
        var start = 0;
        if (chunk.Reset && offset > 0)
        {
            var firstNl = Array.IndexOf(buffer, (byte)'\n', 0, end);
            if (firstNl >= 0) start = firstNl + 1;
        }
        chunk.Text = Encoding.UTF8.GetString(buffer, start, Math.Max(0, end - start));
        chunk.Offset = offset + end;
        return chunk;
    }
}
