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

    public static ScaleChoice[] CreateAll() =>
    [
        new("100", 1.0, Loc.Get("Scale100")),
        new("75", 0.75, Loc.Get("Scale75")),
        new("50", 0.5, Loc.Get("Scale50")),
        new("25", 0.25, Loc.Get("Scale25")),
        new("custom", 0, Loc.Get("ScaleCustom"))
    ];
}
