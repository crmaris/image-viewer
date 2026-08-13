namespace ImageViewer.Editing;

/// <summary>
/// A rotation optionally preceded by a horizontal mirror - the eight ways an image can be oriented.
/// </summary>
/// <remarks>
/// Every orientation of an image is one of exactly eight transforms (the symmetries of a square),
/// and each can be written as "mirror horizontally if needed, then rotate". Having one type for
/// this matters at save time: the pixels on screen already carry the file's EXIF orientation, and
/// the user has since applied their own rotations and flips. Writing the file correctly means
/// composing those into a single transform, not applying them one after another and hoping.
/// </remarks>
public readonly record struct Orientation(bool Mirror, int Rotation)
{
    public static readonly Orientation Identity = new(false, 0);

    public bool IsIdentity => this == Identity;

    /// <summary>True when the transform swaps width and height.</summary>
    public bool SwapsAxes => Rotation is 90 or 270;

    /// <summary>Normalises the rotation into [0, 360).</summary>
    public Orientation Normalized() => new(Mirror, ((Rotation % 360) + 360) % 360);

    /// <summary>Reads an EXIF orientation tag (values 1-8) as a transform.</summary>
    public static Orientation FromExif(int exif) => exif switch
    {
        2 => new Orientation(true, 0),
        3 => new Orientation(false, 180),
        4 => new Orientation(true, 180),
        5 => new Orientation(true, 90),
        6 => new Orientation(false, 90),
        7 => new Orientation(true, 270),
        8 => new Orientation(false, 270),
        _ => Identity,
    };

    /// <summary>Converts back to an EXIF orientation value.</summary>
    public int ToExif() => Normalized() switch
    {
        { Mirror: false, Rotation: 0 } => 1,
        { Mirror: true, Rotation: 0 } => 2,
        { Mirror: false, Rotation: 180 } => 3,
        { Mirror: true, Rotation: 180 } => 4,
        { Mirror: true, Rotation: 90 } => 5,
        { Mirror: false, Rotation: 90 } => 6,
        { Mirror: true, Rotation: 270 } => 7,
        { Mirror: false, Rotation: 270 } => 8,
        _ => 1,
    };

    /// <summary>
    /// Builds the transform the user has applied on top of what they were shown.
    /// </summary>
    /// <remarks>
    /// A vertical flip is a horizontal flip followed by a half turn, so both flips together are
    /// just a half turn with no mirror at all.
    /// </remarks>
    public static Orientation FromUserEdits(bool flipHorizontal, bool flipVertical, int rotation)
    {
        var (mirror, extra) = (flipHorizontal, flipVertical) switch
        {
            (true, true) => (false, 180),
            (true, false) => (true, 0),
            (false, true) => (true, 180),
            _ => (false, 0),
        };

        return new Orientation(mirror, extra + rotation).Normalized();
    }

    /// <summary>
    /// Applies <paramref name="second"/> after this transform, yielding one equivalent transform.
    /// </summary>
    /// <remarks>
    /// Mirroring and rotation do not commute - a mirror reverses the direction of any rotation that
    /// came before it. That is why the second transform's mirror flips the sign of the first's
    /// rotation here, and why naively adding the two rotations together gives the wrong answer for
    /// any image whose EXIF orientation involves a flip.
    /// </remarks>
    public Orientation Then(Orientation second)
    {
        var result = second.Mirror
            ? new Orientation(!Mirror, second.Rotation - Rotation)
            : new Orientation(Mirror, Rotation + second.Rotation);

        return result.Normalized();
    }
}
