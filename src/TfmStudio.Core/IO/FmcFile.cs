using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Core.IO;

/// <summary>
/// Minimal self-describing FMC container (<c>.fmc</c>): the ASCII magic <c>FMC1</c>, an int32
/// little-endian header length, a UTF-8 JSON header (probe, sampling, encoder position), then
/// float32 little-endian samples in <c>[tx][rx][sample]</c> order.
/// </summary>
public static class FmcFile
{
    private static ReadOnlySpan<byte> Magic => "FMC1"u8;

    private sealed record Header(int Elements, double Pitch, double ElementWidth, double CenterFrequency,
        double FractionalBandwidth, double CenterX, double SamplingRate, int Samples, double TimeOffset, double EncoderY);

    public static void Write(Stream stream, FmcData fmc, double encoderY = 0)
    {
        var a = fmc.Array;
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(a.ElementCount, a.Pitch, a.ElementWidth,
            a.CenterFrequency, a.FractionalBandwidth, a.CenterX, fmc.SamplingRate, fmc.SampleCount, fmc.TimeOffset, encoderY));
        stream.Write(Magic);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, header.Length);
        stream.Write(len);
        stream.Write(header);
        var bytes = new byte[fmc.Data.Length * 4];
        for (int i = 0; i < fmc.Data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), fmc.Data[i]);
        stream.Write(bytes);
    }

    public static (FmcData Data, double EncoderY) Read(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("not an FMC1 file");
        Span<byte> len = stackalloc byte[4];
        stream.ReadExactly(len);
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n <= 0 || n > 1 << 20) throw new InvalidDataException("bad header length");
        var json = new byte[n];
        stream.ReadExactly(json);
        var h = JsonSerializer.Deserialize<Header>(json) ?? throw new InvalidDataException("empty header");
        var array = new LinearArray(h.Elements, h.Pitch, h.ElementWidth, h.CenterFrequency, h.FractionalBandwidth, h.CenterX);
        var data = new float[checked(h.Elements * h.Elements * h.Samples)];
        var bytes = new byte[data.Length * 4];
        stream.ReadExactly(bytes);
        for (int i = 0; i < data.Length; i++) data[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return (new FmcData(array, h.SamplingRate, h.Samples, h.TimeOffset, data), h.EncoderY);
    }

    public static void Save(string path, FmcData fmc, double encoderY = 0)
    {
        using var fs = File.Create(path);
        Write(fs, fmc, encoderY);
    }

    public static (FmcData Data, double EncoderY) Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    internal static string Describe(FmcData f) =>
        new StringBuilder().Append(f.ElementCount).Append(" x ").Append(f.ElementCount).Append(" x ").Append(f.SampleCount).ToString();
}
