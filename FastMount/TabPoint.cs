namespace DsoHelper.FastMount;

/// <summary>Saved position of one inventory tab.</summary>
internal sealed class TabPoint
{
    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>Distance from the right edge of the game client, in client-height units.</summary>
    public double? RightRatio { get; set; }

    /// <summary>Distance from the top edge of the game client, in client-height units.</summary>
    public double? TopRatio { get; set; }
}
