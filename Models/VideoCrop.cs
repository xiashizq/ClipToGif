namespace ClipToGif.Models;

/// <summary>相对源视频像素的画面裁切；全幅时不输出 crop 滤镜。</summary>
public sealed class VideoCrop
{
    public const int MinSize = 16;

    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public bool IsFullFrame(int videoWidth, int videoHeight) =>
        videoWidth <= 0 || videoHeight <= 0 ||
        (X <= 0 && Y <= 0 && Width >= videoWidth && Height >= videoHeight);

    public string ToFilter() => $"crop={Width}:{Height}:{X}:{Y}";

    public static VideoCrop Full(int videoWidth, int videoHeight) => new()
    {
        X = 0,
        Y = 0,
        Width = Math.Max(0, videoWidth),
        Height = Math.Max(0, videoHeight)
    };

    public static VideoCrop Clamp(int x, int y, int width, int height, int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
            return Full(0, 0);

        var minW = Math.Min(MinSize, videoWidth);
        var minH = Math.Min(MinSize, videoHeight);

        x = Math.Clamp(x, 0, Math.Max(0, videoWidth - minW));
        y = Math.Clamp(y, 0, Math.Max(0, videoHeight - minH));
        width = Math.Clamp(width, minW, videoWidth - x);
        height = Math.Clamp(height, minH, videoHeight - y);

        return new VideoCrop
        {
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
    }

    public static VideoCrop Normalize(int x, int y, int width, int height, int videoWidth, int videoHeight)
    {
        var crop = Clamp(x, y, width, height, videoWidth, videoHeight);
        x = crop.X & ~1;
        y = crop.Y & ~1;
        width = crop.Width & ~1;
        height = crop.Height & ~1;

        if (width < MinSize && videoWidth >= MinSize)
            width = MinSize & ~1;
        if (height < MinSize && videoHeight >= MinSize)
            height = MinSize & ~1;
        if (width < 2)
            width = Math.Min(2, videoWidth);
        if (height < 2)
            height = Math.Min(2, videoHeight);
        if (x + width > videoWidth)
            width = (videoWidth - x) & ~1;
        if (y + height > videoHeight)
            height = (videoHeight - y) & ~1;

        return new VideoCrop
        {
            X = x,
            Y = y,
            Width = Math.Max(2, width),
            Height = Math.Max(2, height)
        };
    }
}
