using System.Text;

namespace CodexResetGuard.AppServer;

internal sealed class BoundedTextLineReader
{
    private const int BufferLength = 4 * 1_024;

    private readonly TextReader reader;
    private readonly int maximumLineLength;
    private readonly char[] buffer = new char[BufferLength];

    private int bufferOffset;
    private int bufferedCharacters;

    public BoundedTextLineReader(TextReader reader, int maximumLineLength)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineLength);

        this.reader = reader;
        this.maximumLineLength = maximumLineLength;
    }

    public async ValueTask<string?> ReadLineAsync(
        CancellationToken cancellationToken)
    {
        var line = new StringBuilder(
            Math.Min(maximumLineLength, BufferLength),
            maximumLineLength);

        while (true)
        {
            if (bufferOffset == bufferedCharacters)
            {
                bufferedCharacters = await reader.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                bufferOffset = 0;

                if (bufferedCharacters == 0)
                {
                    return line.Length == 0 ? null : line.ToString();
                }
            }

            var newlineIndex = Array.IndexOf(
                buffer,
                '\n',
                bufferOffset,
                bufferedCharacters - bufferOffset);
            var charactersToAppend = newlineIndex >= 0
                ? newlineIndex - bufferOffset
                : bufferedCharacters - bufferOffset;

            if (line.Length > maximumLineLength - charactersToAppend)
            {
                throw new LineLengthLimitExceededException();
            }

            line.Append(buffer, bufferOffset, charactersToAppend);
            bufferOffset += charactersToAppend;

            if (newlineIndex < 0)
            {
                continue;
            }

            bufferOffset++;
            if (line.Length > 0 && line[^1] == '\r')
            {
                line.Length--;
            }

            return line.ToString();
        }
    }
}

internal sealed class LineLengthLimitExceededException : IOException
{
    public LineLengthLimitExceededException()
        : base("The text frame exceeded its configured limit.")
    {
    }
}
