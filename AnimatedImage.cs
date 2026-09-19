using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using ImageMagick;

namespace Atelier;

/// <summary>An animation decoded into ready-to-draw frames and the time each is held for.</summary>
public sealed class AnimatedImage : IDisposable
{
    internal AnimatedImage(List<Bitmap> frames, List<TimeSpan> delays)
    {
        Frames = frames;
        Delays = delays;
    }

    public IReadOnlyList<Bitmap> Frames { get; }

    public IReadOnlyList<TimeSpan> Delays { get; }

    public int FrameCount => Frames.Count;

    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
    }
}

/// <summary>What a cheap look at a file says about whether it is worth decoding.</summary>
public readonly record struct AnimationProbe(bool IsAnimated, int FrameCount, long EstimatedBytes, bool WithinBudget);

/// <summary>
/// Turns an animated GIF or WebP into frames Avalonia can draw.
///
/// Avalonia's Bitmap is a single frame, so without this an animation opens on frame one
/// and stops -- which reads as a broken file rather than an unsupported one.
///
/// Decoded frames are held in memory, and coalescing makes every frame full-size even
/// when the file stores tiny deltas. A long GIF is therefore vastly larger in memory
/// than on disk, so the file is always probed first and an animation past the budget is
/// declined outright, leaving the caller to show the still first frame.
/// </summary>
public static class AnimationDecoder
{
    /// <summary>
    /// How much decoded animation is worth holding. Settable so the tests can push a
    /// file over the line without generating a gigabyte of frames to do it.
    /// </summary>
    public static long MemoryBudgetBytes = 256L * 1024 * 1024;

    /// <summary>
    /// GIF delays are stored in hundredths of a second, and a huge number of files in
    /// the wild claim 0 or 1 -- "as fast as the machine can go", from an era when that
    /// was slow. Every browser has clamped those to 100ms for decades; matching that
    /// is what makes an old GIF play at the speed its author actually saw.
    /// </summary>
    private const uint MinimumSaneDelayCentiseconds = 2;
    private const uint ClampedDelayCentiseconds = 10;

    private static readonly HashSet<string> AnimatableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".gif", ".webp" };

    /// <summary>
    /// Whether a file is even worth opening as an animation, by extension alone. Keeps
    /// the common case -- a JPEG -- from paying for a Magick probe on every load.
    /// </summary>
    public static bool MightAnimate(string path) =>
        AnimatableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Reads the frame count and size without decoding any pixels, and works out
    /// whether the decoded result would fit the budget.
    /// </summary>
    public static AnimationProbe Probe(string path)
    {
        if (!MightAnimate(path) || !File.Exists(path))
            return new AnimationProbe(false, 0, 0, false);

        try
        {
            using var collection = new MagickImageCollection();
            // Ping reads headers only -- the frames themselves are never decoded, which
            // is the entire point of asking before committing the memory.
            collection.Ping(path);

            int frames = collection.Count;
            if (frames <= 1) return new AnimationProbe(false, frames, 0, false);

            var first = collection[0];
            // Coalesced frames are all full canvas size, whatever deltas the file stores.
            long estimated = (long)first.Width * first.Height * 4 * frames;

            return new AnimationProbe(true, frames, estimated, estimated <= MemoryBudgetBytes);
        }
        catch (Exception)
        {
            return new AnimationProbe(false, 0, 0, false);
        }
    }

    /// <summary>
    /// Decodes every frame, or returns null when the file is not an animation or would
    /// not fit the budget. Callers treat null as "show it as a still picture".
    /// </summary>
    public static AnimatedImage? Decode(string path)
    {
        var probe = Probe(path);
        if (!probe.IsAnimated || !probe.WithinBudget) return null;

        try
        {
            using var collection = new MagickImageCollection(path);
            if (collection.Count <= 1) return null;

            // Frames are stored as deltas against what came before, often cropped to
            // just the part that changed. Coalescing replays them into whole images,
            // which is the only form that can be handed to a view one at a time.
            collection.Coalesce();

            var frames = new List<Bitmap>(collection.Count);
            var delays = new List<TimeSpan>(collection.Count);

            foreach (var frame in collection)
            {
                using var buffer = new MemoryStream();
                frame.Write(buffer, MagickFormat.Png);
                buffer.Position = 0;
                frames.Add(new Bitmap(buffer));

                uint centiseconds = frame.AnimationDelay < MinimumSaneDelayCentiseconds
                    ? ClampedDelayCentiseconds
                    : frame.AnimationDelay;
                delays.Add(TimeSpan.FromMilliseconds(centiseconds * 10));
            }

            return new AnimatedImage(frames, delays);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
