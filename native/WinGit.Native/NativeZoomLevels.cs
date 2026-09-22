namespace WinGit.Native;

internal static class NativeZoomLevels
{
    internal const double Default = 1d;

    internal static readonly double[] Supported =
    [
        0.67d,
        0.75d,
        0.8d,
        0.9d,
        1d,
        1.1d,
        1.25d,
        1.5d,
        1.75d,
        2d,
    ];

    internal static double Normalize(double value)
    {
        if (!double.IsFinite(value))
        {
            return Default;
        }

        var closest = Supported[0];
        var closestDistance = Math.Abs(value - closest);
        foreach (var candidate in Supported.Skip(1))
        {
            var distance = Math.Abs(value - candidate);
            if (distance < closestDistance)
            {
                closest = candidate;
                closestDistance = distance;
            }
        }

        return closest;
    }

    internal static double NextIn(double value)
    {
        var current = Normalize(value);
        return Supported.FirstOrDefault(
            candidate => candidate > current,
            current);
    }

    internal static double NextOut(double value)
    {
        var current = Normalize(value);
        for (var index = Supported.Length - 1; index >= 0; index--)
        {
            if (Supported[index] < current)
            {
                return Supported[index];
            }
        }

        return current;
    }

    internal static double Reset() => Default;
}
