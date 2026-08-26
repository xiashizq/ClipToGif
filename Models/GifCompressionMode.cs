using ClipToGif.Localization;

namespace ClipToGif.Models;

public enum GifCompressionMode
{
    None = 0,
    LosslessTransdiff = 1,
    LosslessRectangle = 2,
    LosslessPaletteDiff = 3,
    LossyBayer = 4,
    LossyFloydSteinberg = 5,
    LossyStrong = 6
}

public sealed class CompressionChoice
{
    public GifCompressionMode Mode { get; }
    public string Name { get; }
    public string Description { get; }

    public CompressionChoice(GifCompressionMode mode, string name, string description)
    {
        Mode = mode;
        Name = name;
        Description = description;
    }

    public static CompressionChoice[] CreateAll() =>
    [
        new(GifCompressionMode.None, Loc.Get("CompressionNone"), Loc.Get("CompressionNoneTip")),
        new(GifCompressionMode.LosslessTransdiff, Loc.Get("CompressionLosslessTransdiff"), Loc.Get("CompressionLosslessTransdiffTip")),
        new(GifCompressionMode.LosslessRectangle, Loc.Get("CompressionLosslessRectangle"), Loc.Get("CompressionLosslessRectangleTip")),
        new(GifCompressionMode.LosslessPaletteDiff, Loc.Get("CompressionLosslessPaletteDiff"), Loc.Get("CompressionLosslessPaletteDiffTip")),
        new(GifCompressionMode.LossyBayer, Loc.Get("CompressionLossyBayer"), Loc.Get("CompressionLossyBayerTip")),
        new(GifCompressionMode.LossyFloydSteinberg, Loc.Get("CompressionLossyFloyd"), Loc.Get("CompressionLossyFloydTip")),
        new(GifCompressionMode.LossyStrong, Loc.Get("CompressionLossyStrong"), Loc.Get("CompressionLossyStrongTip"))
    ];
}
