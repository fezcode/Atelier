using Atelier;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// Cover for <see cref="ImageOrientation"/> -- the one transform an image carries,
/// whether it came from an EXIF tag or from the user pressing R.
///
/// The value is read as "mirror horizontally, then rotate clockwise". That order is
/// not arbitrary: it is the order the view applies them (a RenderTransform on the
/// Image, then a LayoutTransform on its host), and under it a rotate is a plain
/// += 90 no matter how the image is mirrored. The other order makes rotation change
/// direction once a mirror is in play, which is exactly the bug users report as
/// "rotate goes the wrong way on flipped photos".
/// </summary>
public class ImageOrientationTests
{
    // ---- EXIF tag mapping -------------------------------------------------

    /// <summary>
    /// The eight EXIF orientations, mapped to the transform that corrects each one.
    /// Tags 5 and 7 are the transpose/transverse diagonals -- the two that are easy
    /// to get backwards, and the reason this is a table rather than a formula.
    /// </summary>
    [Theory]
    [InlineData(1, 0, false)]    // top-left: already correct
    [InlineData(2, 0, true)]     // mirrored
    [InlineData(3, 180, false)]  // upside down
    [InlineData(4, 180, true)]   // mirrored vertically
    [InlineData(5, 270, true)]   // transpose
    [InlineData(6, 90, false)]   // rotated 90 CW -- the common portrait phone photo
    [InlineData(7, 90, true)]    // transverse
    [InlineData(8, 270, false)]  // rotated 270 CW
    public void FromExif_MapsEveryOrientation(int tag, int angle, bool mirrored)
    {
        var o = ImageOrientation.FromExif(tag);

        Assert.Equal(angle, o.Angle);
        Assert.Equal(mirrored, o.Mirrored);
    }

    /// <summary>
    /// A missing, zero or out-of-range tag means "no information", not "broken".
    /// Plenty of PNGs and screenshots carry no orientation at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void FromExif_TreatsAnUnknownTagAsIdentity(int tag)
    {
        Assert.Equal(ImageOrientation.Identity, ImageOrientation.FromExif(tag));
    }

    // ---- Rotation ---------------------------------------------------------

    [Fact]
    public void RotateRight_FourTimes_ReturnsToTheStart()
    {
        var o = ImageOrientation.Identity;

        for (int i = 0; i < 4; i++) o = o.RotateRight();

        Assert.Equal(ImageOrientation.Identity, o);
    }

    [Fact]
    public void RotateLeft_FourTimes_ReturnsToTheStart()
    {
        var o = ImageOrientation.Identity;

        for (int i = 0; i < 4; i++) o = o.RotateLeft();

        Assert.Equal(ImageOrientation.Identity, o);
    }

    [Fact]
    public void RotateRight_ThenRotateLeft_Cancels()
    {
        var start = ImageOrientation.FromExif(6);

        Assert.Equal(start, start.RotateRight().RotateLeft());
    }

    /// <summary>
    /// The property the "mirror first" convention buys: rotating right always adds
    /// 90 degrees and never touches the mirror, even on an already-mirrored image.
    /// </summary>
    [Fact]
    public void RotateRight_OnAMirroredImage_StillTurnsClockwise()
    {
        var mirrored = ImageOrientation.FromExif(2);

        var turned = mirrored.RotateRight();

        Assert.Equal(90, turned.Angle);
        Assert.True(turned.Mirrored);
    }

    [Fact]
    public void Angle_StaysWithinASingleTurn()
    {
        var o = ImageOrientation.Identity.RotateRight().RotateRight().RotateRight().RotateRight().RotateRight();

        Assert.Equal(90, o.Angle);
    }

    [Fact]
    public void RotateLeft_FromZero_WrapsTo270()
    {
        Assert.Equal(270, ImageOrientation.Identity.RotateLeft().Angle);
    }

    // ---- Flips ------------------------------------------------------------

    [Fact]
    public void FlipHorizontal_Twice_Cancels()
    {
        var start = ImageOrientation.FromExif(6);

        Assert.Equal(start, start.FlipHorizontal().FlipHorizontal());
    }

    [Fact]
    public void FlipVertical_Twice_Cancels()
    {
        var start = ImageOrientation.FromExif(6);

        Assert.Equal(start, start.FlipVertical().FlipVertical());
    }

    /// <summary>
    /// Mirroring an upright image is the whole of the transform; mirroring a rotated
    /// one has to reverse the rotation too, or the result is mirrored about the wrong
    /// axis. This is the algebra the naive implementation gets wrong.
    /// </summary>
    [Fact]
    public void FlipHorizontal_OnARotatedImage_ReversesTheAngle()
    {
        var rotated = ImageOrientation.FromExif(6); // 90, not mirrored

        var flipped = rotated.FlipHorizontal();

        Assert.Equal(270, flipped.Angle);
        Assert.True(flipped.Mirrored);
    }

    [Fact]
    public void FlipVertical_IsAHorizontalFlipPlusAHalfTurn()
    {
        var o = ImageOrientation.Identity.FlipVertical();

        Assert.Equal(180, o.Angle);
        Assert.True(o.Mirrored);
    }

    /// <summary>Flipping both ways is a half turn -- the image is upright again, not mirrored.</summary>
    [Fact]
    public void FlipHorizontal_ThenFlipVertical_IsAHalfTurn()
    {
        var o = ImageOrientation.Identity.FlipHorizontal().FlipVertical();

        Assert.Equal(180, o.Angle);
        Assert.False(o.Mirrored);
    }

    // ---- Consequences for layout -----------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(180, false)]
    [InlineData(0, true)]
    [InlineData(180, true)]
    public void SwapsDimensions_IsFalse_ForUprightAndUpsideDown(int angle, bool mirrored)
    {
        Assert.False(new ImageOrientation(angle, mirrored).SwapsDimensions);
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(270, false)]
    [InlineData(90, true)]
    [InlineData(270, true)]
    public void SwapsDimensions_IsTrue_OnItsSide(int angle, bool mirrored)
    {
        Assert.True(new ImageOrientation(angle, mirrored).SwapsDimensions);
    }

    [Fact]
    public void IsIdentity_DistinguishesAnUntouchedImage()
    {
        Assert.True(ImageOrientation.Identity.IsIdentity);
        Assert.False(ImageOrientation.Identity.RotateRight().IsIdentity);
        Assert.False(ImageOrientation.Identity.FlipHorizontal().IsIdentity);
    }
}
