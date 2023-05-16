namespace FMV2FM2;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal class Program
{
    static void Main(string[] args)
    {
        var data = File.ReadAllBytes(args[1]);
        var recording = Recording.FromFmv(
            Path.GetFileNameWithoutExtension(args[0]),
            GetMd5(args[0]),
            data);

        var result = recording.ToFm2();
        File.WriteAllBytes(Path.ChangeExtension(args[1], ".fm2"), result);
    }

    static string GetMd5(string path)
    {
        var data = File.ReadAllBytes(path);
        using var md5 = MD5.Create();
        return Convert.ToBase64String(md5.ComputeHash(data, 0x10, data.Length - 0x10));
    }
}

public struct Comment
{
    public string Subject;
    public string Content;
}

public struct Subtitle
{
    public int Frame;
    public string Content;
}

public class Recording
{
    private Recording(
        string romFileName,
        string md5Hash,
        byte[] input)
        : this(
              romFileName,
              md5Hash,
              Array.Empty<Comment>(),
              Array.Empty<Subtitle>(),
              input)
    { }

    private Recording(
        string romFileName,
        string md5Hash,
        Comment[] comments,
        byte[] input)
        : this(romFileName, md5Hash, comments, Array.Empty<Subtitle>(), input)
    { }

    private Recording(
        string romFileName,
        string romChecksum,
        Comment[] comments,
        Subtitle[] subtitles,
        byte[] input)
    {
        RomFileName = romFileName;
        Comments = comments;
        Subtitles = subtitles;
        RomChecksum = romChecksum;
        Guid = Guid.NewGuid();
        Input = input;
    }

    public int Version { get; init; }

    public int EmuVersion { get; init; }

    public int RerecordCount { get; init; }

    public bool PalFlag { get; init; }

    public bool NewPpu { get; init; }

    public bool Fds { get; init; }

    public bool Fourscore { get; init; }

    public byte Port0 { get; init; }

    public byte Port1 { get; init; }

    public byte Port2 { get; init; }

    public bool Binary { get; init; }

    public string RomFileName { get; }

    public Guid Guid { get; }

    private string RomChecksum { get; }

    private Comment[] Comments { get; }

    private Subtitle[] Subtitles { get; }

    private byte[] Input { get; }

    public static Recording FromFmv(string romFileName, string romChecksum, byte[] data)
    {
        const int SignatureStringLength = 4;
        const int EmulatorIndeitifierStringLength = 0x40;
        const int MovieTitleStringLength = 0x40;

        const int SignatureStringStartIndex = 0x000;
        const int SavestateFlagIndex = 0x004;
        const int InputModeOffset = 0x005;
        const int RerecordCountOffset = 0x00A;
        const int EmulatorIdentifierStringOffset = 0x010;
        const int MovieTitleStringoffset = 0x050;
        const int FrameDataOffset = 0x090;

        if (data.Length < FrameDataOffset)
        {
            throw new ArgumentException(
                "FMV data does not meet minimum size for a valid header.");
        }

        var signature = Encoding.ASCII.GetString(
            data,
            SignatureStringStartIndex,
            SignatureStringLength);
        if (signature != "FMV\x1A")
        {
            throw new ArgumentException(
                "FMV file does not have valid header signature");
        }

        if ((data[SavestateFlagIndex] & 0x80) != 0)
        {
            throw new ArgumentException(
                "Reading FMV files that record from savestates are not yet " +
                "implemented.");
        }

        var inputMode = data[InputModeOffset];
        var rerecordCount = BitConverter.ToUInt32(data, RerecordCountOffset) + 1;
        var emulatorIdentifier = Encoding.ASCII.GetString(
            data,
            EmulatorIdentifierStringOffset,
            EmulatorIndeitifierStringLength);
        var movieTitle = Encoding.ASCII.GetString(
            data,
            MovieTitleStringoffset,
            MovieTitleStringLength);

        var bytesPerFrame = 0;
        var controllerEnabled = new int[3] { -1, -1, -1 };
        for (var i = 0; i < controllerEnabled.Length; i++)
        {
            if ((inputMode & (1 << (7 - i))) != 0)
            {
                controllerEnabled[bytesPerFrame++] = i;
            }
        }

        if (bytesPerFrame == 0 && data.Length > FrameDataOffset)
        {
            throw new ArgumentException(
                "FMV file does not specify which controller(s) are recorded.");
        }

        if (((data.Length - FrameDataOffset) % bytesPerFrame) != 0)
        {
            throw new ArgumentException(
                "FMV file recording data seems corrupted. Multiple controllers were" +
                "set to record, but data does not match this configuration.");
        }

        var bitChanges = new byte[] { 7, 6, 4, 5, 1, 0, 2, 3 };
        var byteChanges = new byte[0x100];
        for (var i = 0; i < byteChanges.Length; i++)
        {
            for (var j = 0; j < bitChanges.Length; j++)
            {
                if ((i & (1 << j)) != 0)
                {
                    byteChanges[i] |= (byte)(1 << bitChanges[j]);
                }
            }
        }

        var frames = (data.Length - FrameDataOffset) / bytesPerFrame;
        var input = new byte[frames * (1 + bytesPerFrame)];
        for (var i = 0; i < frames; i++)
        {
            for (var j = 0; j < bytesPerFrame; j++)
            {
                var value = byteChanges[data[(i * bytesPerFrame) + j + FrameDataOffset]];
                input[j + 1 + (i * (1 + bytesPerFrame))] = value;
            }
        }

        var comments = new Comment[2]
        {
            new() { Subject = "famtasiaEmulatorIdentifier", Content = emulatorIdentifier },
            new() { Subject = "famtasiaMovieTitle", Content = movieTitle },
        };

        return new Recording(romFileName, romChecksum, comments, input)
        {
            Version = 3,
            EmuVersion = 22020,
            RerecordCount = (int)rerecordCount,
            Port0 = (byte)(controllerEnabled[0] >= 0 ? 1 : 0),
            Port1 = (byte)(controllerEnabled[1] >= 0 ? 1 : 0),
            Binary = true,
        };
    }

    public byte[] ToFm2()
    {
        var bytesPerFrame = 1;
        var ports = new byte[3] { Port0, Port1, Port2 };
        foreach (var port in ports)
        {
            if (port == 1)
            {
                bytesPerFrame++;
            }
        }

        var sb = new StringBuilder()
            .Append("version ").Append(Version).Append('\n')
            .Append("emuVersion ").Append(22020).Append('\n')
            .Append("rerecordCount ").Append(RerecordCount).Append('\n')
            .Append("palFlag ").Append(PalFlag ? 1 : 0).Append('\n')
            .Append("NewPPU ").Append(NewPpu ? 1 : 0).Append('\n')
            .Append("FDS ").Append(Fds ? 1 : 0).Append('\n')
            .Append("fourscore ").Append(Fourscore ? 1 : 0).Append('\n')
            .Append("port0 ").Append(Port0).Append('\n')
            .Append("port1 ").Append(Port1).Append('\n')
            .Append("port2 ").Append(Port2).Append('\n')
            .Append("binary ").Append(Binary ? 1 : 0).Append('\n')
            .Append("length ").Append(Input.Length / bytesPerFrame).Append('\n')
            .Append("romFilename ").Append(RomFileName).Append('\n');

        foreach (var comment in Comments)
        {
            //_ = sb.Append("comment ").Append(comment.Subject).Append(' ').Append(
            //    comment.Content).Append('\n');
        }

        foreach (var subtitle in Subtitles)
        {
            _ = sb.Append("subtitle ").Append(subtitle.Frame).Append(' ').Append(
                subtitle.Content).Append('\n');
        }

        var header = sb.Append("guid ").Append(Guid).Append('\n')
            .Append("romChecksum base64:").Append(RomChecksum).Append('\n')
            .Append("savestate 0").Append('\n')
            .Append('|').ToString();

        var result = new List<byte>();
        result.AddRange(Encoding.ASCII.GetBytes(header));

        if (!Binary)
        {
            throw new NotSupportedException();
        }
        else
        {
            result.AddRange(Input);
        }

        return result.ToArray();
    }
}

[Flags]
enum Input : byte
{
    Right = 0x01,
    Left = 0x02,
    Up = 0x04,
    Down = 0x08,
    B = 0x10,
    A = 0x20,
    Select = 0x40,
    Start = 0x80,
}
