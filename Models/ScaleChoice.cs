using ClipToGif.Localization;

namespace ClipToGif.Models;

public sealed class ScaleChoice
{
    public string Id { get; }
    public double Factor { get; }
    public string Name { get; }

    public ScaleChoice(string id, double factor, string name)
    {
        Id = id;
        Factor = factor;
        Name = name;
    }

    public static ScaleChoice[] CreateAll(int? customPercent = null)
    {
        var customName = customPercent is >= 1 and <= 99
            ? Loc.Format("ScaleCustomPercent", customPercent.Value)
            : Loc.Get("ScaleCustom");
        var customFactor = customPercent is >= 1 and <= 99 ? customPercent.Value / 100.0 : 0;
        return
        [
            new("100", 1.0, Loc.Get("Scale100")),
            new("75", 0.75, Loc.Get("Scale75")),
            new("50", 0.5, Loc.Get("Scale50")),
            new("25", 0.25, Loc.Get("Scale25")),
            new("custom", customFactor, customName)
        ];
    }
}
