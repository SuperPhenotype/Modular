using System.Text;

namespace Modular.Core.Installers.Fallout4;

/// <summary>
/// What a Fallout 4 plugin's header (its <c>TES4</c> record) says: the plugins it needs
/// loaded first, and whether it is a master or a light plugin.
/// </summary>
/// <param name="Masters">The plugins it depends on, in the order the header lists them.</param>
/// <param name="IsMaster">ESM flag or <c>.esm</c>: the engine loads it before every non-master.</param>
/// <param name="IsLight">ESL flag or <c>.esl</c>: it takes no full load-order slot.</param>
public sealed record Fallout4Plugin(IReadOnlyList<string> Masters, bool IsMaster, bool IsLight)
{
    private const uint EsmFlag = 0x1;
    private const uint EslFlag = 0x200;

    /// <summary>The record header's size in Fallout 4 (and Skyrim): type, size, flags, id, VC, version.</summary>
    private const int RecordHeaderSize = 24;

    /// <summary>Largest header read; real ones are a few KB even with a hundred masters.</summary>
    private const int MaxHeaderBytes = 1024 * 1024;

    private static readonly Encoding Windows1252 = CodePagesEncoding();

    /// <summary>Whether a file name is a plugin the engine loads.</summary>
    public static bool IsPluginFile(string fileName) =>
        fileName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a plugin's header from disk; null when it isn't a readable plugin.</summary>
    public static Fallout4Plugin? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a plugin's header; null when the stream doesn't start with a <c>TES4</c> record.</summary>
    /// <param name="stream">The plugin, positioned at its start.</param>
    /// <param name="fileName">Its file name: the extension also marks masters and light plugins.</param>
    public static Fallout4Plugin? Read(Stream stream, string fileName)
    {
        var header = new byte[RecordHeaderSize];
        if (!ReadExactly(stream, header))
            return null;
        if (Encoding.ASCII.GetString(header, 0, 4) != "TES4")
            return null;

        var dataSize = BitConverter.ToUInt32(header, 4);
        var flags = BitConverter.ToUInt32(header, 8);
        if (dataSize > MaxHeaderBytes)
            return null;

        var data = new byte[dataSize];
        if (!ReadExactly(stream, data))
            return null;

        var masters = new List<string>();
        var offset = 0;
        uint? nextSize = null;
        while (offset + 6 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data, offset, 4);
            uint size = BitConverter.ToUInt16(data, offset + 4);
            offset += 6;
            if (nextSize is { } big)
            {
                size = big;
                nextSize = null;
            }
            if (offset + size > data.Length)
                break;
            if (type == "XXXX" && size == 4)
                nextSize = BitConverter.ToUInt32(data, offset); // the next subrecord's real size
            else if (type == "MAST")
                masters.Add(Windows1252.GetString(data, offset, (int)size).TrimEnd('\0'));
            offset += (int)size;
        }

        var isLight = (flags & EslFlag) != 0 || fileName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
        // An .esl file loads as a master; an ESL-flagged .esp ("ESPFE") stays a normal plugin.
        var isMaster = (flags & EsmFlag) != 0 ||
                       fileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
        return new Fallout4Plugin(masters, isMaster, isLight);
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private static Encoding CodePagesEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}
