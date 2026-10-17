using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Atelier;

/// <summary>
/// Removes the metadata a picture carries -- EXIF (camera, dates, GPS), XMP, IPTC,
/// comments and C2PA Content Credentials -- for File &gt; Remove Metadata.
///
/// Two things are deliberately kept. The colour profile is not metadata in the privacy
/// sense: drop it and a wide-gamut photo turns dull. And the orientation survives as a
/// one-tag EXIF block, because a portrait phone photo stripped of its tag lands on its
/// side in every viewer, which reads as "removing metadata broke my picture".
///
/// JPEG, PNG and WebP are rewritten at the container level, so the compressed pixels
/// are copied byte for byte and nothing is re-encoded. Anything else goes through
/// Magick, which does mean a re-encode; HEIC is refused because this build of Magick
/// cannot write it.
/// </summary>
public static class MetadataStripper
{
    /// <summary>Whether <paramref name="path"/> is a kind of file this can clean.</summary>
    public static bool CanStrip(string path) => Path.GetExtension(path).ToLowerInvariant() is not
        (".svg" or ".heic" or ".heif" or "");

    /// <summary>
    /// Writes a metadata-free copy of <paramref name="source"/> to
    /// <paramref name="destination"/>, which may be the same file. The write goes to a
    /// temporary file first, so a failure part way leaves the original untouched.
    /// </summary>
    public static void Strip(string source, string destination)
    {
        string ext = Path.GetExtension(source).ToLowerInvariant();
        if (!CanStrip(source))
            throw new NotSupportedException($"Removing metadata from {ext.TrimStart('.').ToUpperInvariant()} files is not supported.");

        int orientation = ReadOrientationTag(source);
        byte[] data = File.ReadAllBytes(source);

        string dir = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? ".";
        string temp = Path.Combine(dir, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[]? cleaned = data.Length >= 12 ? StripContainer(data, orientation) : null;
            if (cleaned != null) File.WriteAllBytes(temp, cleaned);
            else StripWithMagick(source, temp);

            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The cleaned bytes for a JPEG, PNG or WebP, or null for any other container.</summary>
    internal static byte[]? StripContainer(byte[] data, int orientation)
    {
        if (data[0] == 0xFF && data[1] == 0xD8) return StripJpeg(data, orientation);
        if (data.AsSpan(0, 8).SequenceEqual(PngSignature)) return StripPng(data, orientation);
        if (Ascii(data, 0, 4) == "RIFF" && Ascii(data, 8, 4) == "WEBP") return StripWebP(data, orientation);
        return null;
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // ---- JPEG ---------------------------------------------------------------------

    /// <summary>
    /// Keeps APP0 (JFIF), the ICC profile in APP2 and Adobe's APP14 -- that last one
    /// says how the colour channels are stored, and a CMYK JPEG without it decodes
    /// inverted. Every other APPn segment and every comment goes. Writing stops at the
    /// first EOI, which also drops what phones append after it: MPF secondary images
    /// and gain maps, each with its own copy of the EXIF.
    /// </summary>
    private static byte[] StripJpeg(byte[] data, int orientation)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        bool orientationWritten = orientation is <= 1 or > 8;

        int pos = 2;
        while (pos + 1 < data.Length)
        {
            if (data[pos] != 0xFF) throw new InvalidDataException("Malformed JPEG");
            byte marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; }

            if (marker == 0xD9)
            {
                output.Write(data, pos, 2);
                break;
            }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.Write(data, pos, 2);
                pos += 2;
                continue;
            }

            if (pos + 4 > data.Length) throw new InvalidDataException("Truncated JPEG");
            int length = (data[pos + 2] << 8) | data[pos + 3];
            int end = pos + 2 + length;
            if (length < 2 || end > data.Length) throw new InvalidDataException("Truncated JPEG");

            if (!orientationWritten && marker != 0xE0)
            {
                WriteJpegOrientation(output, orientation);
                orientationWritten = true;
            }

            if (KeepJpegSegment(marker, data, pos + 4, end))
                output.Write(data, pos, end - pos);

            pos = end;
            if (marker == 0xDA)
            {
                // Entropy-coded data runs until the next marker that is not a stuffed
                // byte (FF00), a restart (FFD0-D7) or fill (FFFF).
                int scan = pos;
                while (scan + 1 < data.Length)
                {
                    if (data[scan] == 0xFF)
                    {
                        byte next = data[scan + 1];
                        if (next != 0x00 && next != 0xFF && (next < 0xD0 || next > 0xD7)) break;
                    }
                    scan++;
                }
                output.Write(data, pos, scan - pos);
                pos = scan;
            }
        }
        return output.ToArray();
    }

    private static bool KeepJpegSegment(byte marker, byte[] data, int start, int end)
    {
        if (marker == 0xFE) return false;                       // COM
        if (marker < 0xE0 || marker > 0xEF) return true;        // tables, frame, scan
        if (marker == 0xE0) return true;                        // JFIF
        if (marker == 0xE2) return StartsWith(data, start, end, "ICC_PROFILE\0");
        if (marker == 0xEE) return StartsWith(data, start, end, "Adobe");
        return false;                                           // EXIF, XMP, IPTC, C2PA, MPF...
    }

    private static void WriteJpegOrientation(Stream output, int orientation)
    {
        byte[] tiff = OrientationTiff(orientation);
        int length = 2 + 6 + tiff.Length;
        output.Write(new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length });
        output.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
        output.Write(tiff);
    }

    // ---- PNG ----------------------------------------------------------------------

    /// <summary>Chunks that describe rather than draw: text, EXIF, timestamps, C2PA.</summary>
    private static readonly HashSet<string> PngMetadataChunks = new() { "eXIf", "tEXt", "zTXt", "iTXt", "tIME", "caBX" };

    private static byte[] StripPng(byte[] data, int orientation)
    {
        using var output = new MemoryStream(data.Length);
        output.Write(PngSignature);
        bool orientationWritten = orientation is <= 1 or > 8;

        int pos = 8;
        while (pos + 12 <= data.Length)
        {
            int length = (int)ReadU32(data, pos);
            if (length < 0 || pos + 12 + length > data.Length) throw new InvalidDataException("Truncated PNG");
            string type = Ascii(data, pos + 4, 4);

            // eXIf has to come before the image data.
            if (!orientationWritten && type == "IDAT")
            {
                WritePngChunk(output, "eXIf", OrientationTiff(orientation));
                orientationWritten = true;
            }

            if (!PngMetadataChunks.Contains(type))
                output.Write(data, pos, 12 + length);

            pos += 12 + length;
            if (type == "IEND") break;
        }
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] payload)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        WriteU32(output, (uint)payload.Length);
        output.Write(typeBytes);
        output.Write(payload);
        WriteU32(output, Crc32(typeBytes.Concat(payload).ToArray()));
    }

    // ---- WebP ---------------------------------------------------------------------

    private static byte[] StripWebP(byte[] data, int orientation)
    {
        var chunks = new List<(string Type, byte[] Payload)>();
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            string type = Ascii(data, pos, 4);
            int length = (int)ReadU32LE(data, pos + 4);
            if (length < 0 || pos + 8 + length > data.Length) throw new InvalidDataException("Truncated WebP");
            if (type is not ("EXIF" or "XMP " or "C2PA"))
                chunks.Add((type, data[(pos + 8)..(pos + 8 + length)]));
            pos += 8 + length + (length & 1);
        }

        int vp8x = chunks.FindIndex(c => c.Type == "VP8X");
        if (vp8x >= 0)
        {
            var header = chunks[vp8x].Payload;
            header[0] &= unchecked((byte)~(0x08 | 0x04));       // EXIF and XMP flags
            // A simple-format WebP has no VP8X, so it cannot say it carries EXIF; the
            // tag only goes back where there is a header to announce it.
            if (orientation is > 1 and <= 8)
            {
                header[0] |= 0x08;
                chunks.Add(("EXIF", OrientationTiff(orientation)));
            }
        }

        using var output = new MemoryStream(data.Length);
        output.Write(Encoding.ASCII.GetBytes("RIFF"));
        WriteU32LE(output, 0);
        output.Write(Encoding.ASCII.GetBytes("WEBP"));
        foreach (var (type, payload) in chunks)
        {
            output.Write(Encoding.ASCII.GetBytes(type));
            WriteU32LE(output, (uint)payload.Length);
            output.Write(payload);
            if ((payload.Length & 1) == 1) output.WriteByte(0);
        }
        var bytes = output.ToArray();
        uint riffSize = (uint)(bytes.Length - 8);
        bytes[4] = (byte)riffSize; bytes[5] = (byte)(riffSize >> 8); bytes[6] = (byte)(riffSize >> 16); bytes[7] = (byte)(riffSize >> 24);
        return bytes;
    }

    // ---- Everything else ------------------------------------------------------------

    /// <summary>
    /// A re-encode through Magick. Every frame is kept, so an animated GIF stays
    /// animated; the orientation is baked into the pixels, since there is no telling
    /// whether the format has anywhere to keep the tag.
    /// </summary>
    private static void StripWithMagick(string source, string destination)
    {
        using var images = new MagickImageCollection(source);
        var format = images[0].Format;
        foreach (var image in images)
        {
            var icc = image.GetColorProfile();
            if (images.Count == 1) image.AutoOrient();
            image.Strip();
            if (icc != null) image.SetProfile(icc);
        }
        images.Write(destination, format);
    }

    // ---- Helpers ----------------------------------------------------------------------

    private static int ReadOrientationTag(string path)
    {
        try
        {
            foreach (var directory in ImageMetadataReader.ReadMetadata(path).OfType<ExifIfd0Directory>())
                if (directory.TryGetInt32(ExifDirectoryBase.TagOrientation, out int tag)) return tag;
        }
        catch { }
        return 1;
    }

    /// <summary>A big-endian TIFF block holding a single IFD0 entry: Orientation.</summary>
    internal static byte[] OrientationTiff(int orientation) => new byte[]
    {
        (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x01,
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    };

    private static bool StartsWith(byte[] data, int start, int end, string prefix) =>
        end - start >= prefix.Length && Encoding.ASCII.GetString(data, start, prefix.Length) == prefix;

    private static string Ascii(byte[] data, int start, int length) =>
        start + length <= data.Length ? Encoding.ASCII.GetString(data, start, length) : "";

    private static uint ReadU32(byte[] d, int p) =>
        (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

    private static uint ReadU32LE(byte[] d, int p) =>
        (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24);

    private static void WriteU32(Stream s, uint v) =>
        s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    private static void WriteU32LE(Stream s, uint v) =>
        s.Write(new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) });

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] bytes)
    {
        var table = _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
