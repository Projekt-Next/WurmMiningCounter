namespace WurmMiningCounter.Models;

public sealed class WindowPlacement
{
    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public WindowPlacement(double left, double top, double width)
    {
        Left = left;
        Top = top;
        Width = width;
    }
}
