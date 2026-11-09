using TfmStudio.Core.Physics;

namespace TfmStudio.Core.Imaging;

/// <summary>One phased-array focal law: element firing (or receive) delays for a steered, focused beam.</summary>
/// <param name="Angle">Steering angle from the surface normal, rad (positive towards +x).</param>
/// <param name="FocalRange">Sound path from the array centre to the focal point, m (∞ = unfocused).</param>
/// <param name="Delays">Delay of each element, s (minimum is 0).</param>
/// <param name="MaxPath">Longest element-to-focus path, m; at time MaxPath/c after the first element fires, all wavefronts reach the focus together.</param>
public sealed record FocalLaw(double Angle, double FocalRange, double[] Delays, double MaxPath);

/// <summary>Focal-law calculator for a contact linear array (direct contact, no wedge).</summary>
public static class FocalLaws
{
    /// <summary>
    /// Delays that steer the beam to <paramref name="angle"/> and focus it at
    /// <paramref name="focalRange"/> along the beam: <c>δ_n = (max_m |f − e_m| − |f − e_n|) / c</c>.
    /// For an infinite focal range this reduces to linear steering, <c>δ_n ∝ x_n·sinθ / c</c>.
    /// </summary>
    public static FocalLaw Compute(LinearArray array, double velocity, double angle, double focalRange)
    {
        int n = array.ElementCount;
        var delays = new double[n];
        double sin = Math.Sin(angle), cos = Math.Cos(angle);
        if (double.IsPositiveInfinity(focalRange))
        {
            for (int e = 0; e < n; e++) delays[e] = (array.ElementX(e) - array.CenterX) * sin / velocity;
            double min = delays.Min();
            for (int e = 0; e < n; e++) delays[e] -= min;
            return new FocalLaw(angle, focalRange, delays, double.NaN);
        }
        double fx = array.CenterX + focalRange * sin, fz = focalRange * cos;
        var path = new double[n];
        for (int e = 0; e < n; e++) path[e] = Math.Sqrt(Math.Pow(fx - array.ElementX(e), 2) + fz * fz);
        double max = path.Max();
        for (int e = 0; e < n; e++) delays[e] = (max - path[e]) / velocity;
        return new FocalLaw(angle, focalRange, delays, max);
    }

    /// <summary>Sectorial scan: one focal law per angle from <paramref name="minAngle"/> to <paramref name="maxAngle"/>.</summary>
    public static IReadOnlyList<FocalLaw> Sector(LinearArray array, double velocity, double minAngle, double maxAngle,
        double step, double focalRange)
    {
        int count = (int)Math.Round((maxAngle - minAngle) / step) + 1;
        var laws = new FocalLaw[count];
        for (int i = 0; i < count; i++) laws[i] = Compute(array, velocity, minAngle + i * step, focalRange);
        return laws;
    }
}
