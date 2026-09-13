using System.Text;

namespace AIUsageMonitor.Core.Infrastructure;

/// <summary>Reads the lines of a UTF-8 text file starting from the end, one chunk at a time.</summary>
public static class ReverseLineReader
{
    public static IEnumerable<string> ReadLinesFromEnd(string path, int chunkSize = 64 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long position = stream.Length;
        var chunk = new byte[chunkSize];
        // Bytes that precede the first newline seen so far (the head of a line that continues in earlier chunks).
        byte[] carry = Array.Empty<byte>();

        while (position > 0)
        {
            int toRead = (int)Math.Min(chunkSize, position);
            position -= toRead;
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(chunk, 0, toRead);

            var buffer = new byte[toRead + carry.Length];
            Buffer.BlockCopy(chunk, 0, buffer, 0, toRead);
            Buffer.BlockCopy(carry, 0, buffer, toRead, carry.Length);

            int end = buffer.Length;
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;
                var line = Decode(buffer, i + 1, end - i - 1);
                if (line.Length > 0) yield return line;
                end = i;
            }

            carry = new byte[end];
            Buffer.BlockCopy(buffer, 0, carry, 0, end);
        }

        if (carry.Length > 0)
        {
            var line = Decode(carry, 0, carry.Length);
            if (line.Length > 0) yield return line;
        }
    }

    private static string Decode(byte[] bytes, int offset, int count) =>
        count <= 0 ? string.Empty : Encoding.UTF8.GetString(bytes, offset, count).TrimEnd('\r');
}
