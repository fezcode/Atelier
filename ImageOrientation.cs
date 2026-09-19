using System;

namespace Atelier;

/// <summary>
/// How an image is turned on screen: mirror horizontally, then rotate clockwise.
///
/// One value covers both sources of rotation. A photo arrives with an EXIF
/// orientation tag -- a phone writes the sensor's raw landscape frame and records
/// "this is really a portrait" beside it -- and that tag is simply the starting
/// value of the same transform the user then drives with Rotate Right. There is no
/// separate auto-orient step to keep in sync with the manual one.
///
/// The "mirror first, then rotate" order matters. It is the order the view applies
/// them (a RenderTransform on the Image, then the LayoutTransform on its host), and
/// under it <see cref="RotateRight"/> is a plain +90 whatever the mirror is doing.
/// Compose them the other way round and rotation reverses direction on a mirrored
/// image, which reads to the user as "rotate goes the wrong way on flipped photos".
///
/// These are the eight symmetries of a rectangle, so every operation here lands back
/// on one of the eight -- the type is closed, and there is nothing to normalise
/// beyond keeping the angle inside a single turn.
/// </summary>
/// <param name="Angle">Clockwise degrees: 0, 90, 180 or 270.</param>
/// <param name="Mirrored">Whether the image is mirrored horizontally before the rotation.</param>
public readonly record struct ImageOrientation(int Angle, bool Mirrored)
{
    /// <summary>An image shown exactly as it is stored.</summary>
    public static readonly ImageOrientation Identity = new(0, false);

    public bool IsIdentity => Angle == 0 && !Mirrored;

    /// <summary>
    /// True on its side, where the displayed width is the stored height. Fit-to-view
    /// and the centring maths have to ask before dividing by a dimension.
    /// </summary>
    public bool SwapsDimensions => Angle == 90 || Angle == 270;

    /// <summary>
    /// The transform that corrects an EXIF orientation tag (1-8). Anything else --
    /// absent, zero, out of range -- means the file said nothing, which is not an
    /// error: most PNGs and screenshots carry no tag at all.
    ///
    /// Tags 5 and 7 are the diagonal reflections (transpose and transverse). They are
    /// rare enough to be the ones every implementation gets backwards, which is why
    /// this is a literal table and not a formula.
    /// </summary>
    public static ImageOrientation FromExif(int tag) => tag switch
    {
        2 => new ImageOrientation(0, true),
        3 => new ImageOrientation(180, false),
        4 => new ImageOrientation(180, true),
        5 => new ImageOrientation(270, true),
        6 => new ImageOrientation(90, false),
        7 => new ImageOrientation(90, true),
        8 => new ImageOrientation(270, false),
        _ => Identity,
    };

    public ImageOrientation RotateRight() => this with { Angle = Normalise(Angle + 90) };

    public ImageOrientation RotateLeft() => this with { Angle = Normalise(Angle - 90) };

    /// <summary>
    /// Mirrors what is on screen left-to-right.
    ///
    /// Because the stored mirror happens *before* the rotation, adding one to the end
    /// has to be pushed back through the turn -- reflecting a rotation reverses it.
    /// Hence the angle negates. Without that the image comes back mirrored about the
    /// wrong axis whenever it was rotated.
    /// </summary>
    public ImageOrientation FlipHorizontal() => new(Normalise(-Angle), !Mirrored);

    /// <summary>Mirrors what is on screen top-to-bottom: a horizontal flip plus a half turn.</summary>
    public ImageOrientation FlipVertical() => new(Normalise(180 - Angle), !Mirrored);

    private static int Normalise(int angle) => ((angle % 360) + 360) % 360;
}
