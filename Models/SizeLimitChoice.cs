using ClipToGif.Localization;

namespace ClipToGif.Models;

public sealed class SizeLimitChoice
{
    public string Id { get; }
    public long MaxBytes { get; }
    public string Name { get; }
    public string Description { get; }

    public SizeLimitChoice(string id, long maxBytes, string name, string description)
    {
        Id = id;
        MaxBytes = maxBytes;
        Name = name;
        Description = description;
    }

    public static SizeLimitChoice[] CreateAll() =>
    [
        new("none", 0, Loc.Get("SizeLimitNone"), Loc.Get("SizeLimitNoneTip")),
        new("wechat", 10L * 1024 * 1024, Loc.Get("SizeLimitWechat"), Loc.Get("SizeLimitWechatTip"))
    ];
}
