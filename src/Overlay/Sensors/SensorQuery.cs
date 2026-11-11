using LibreHardwareMonitor.Hardware;

namespace Overlay.Sensors;

internal static class SensorQuery
{
    /// <summary>First sensor of <paramref name="type"/> matching one of <paramref name="names"/>, in priority order.</summary>
    public static ISensor? Find(this IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
            foreach (var s in hw.Sensors)
                if (s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return s;
        return null;
    }

    public static ISensor? FirstOf(this IHardware hw, SensorType type) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type);

    public static float? Read(this ISensor? sensor) =>
        sensor?.Value is float v && float.IsFinite(v) ? v : null;

    public static IEnumerable<IHardware> WithSubHardware(this IHardware hw) =>
        hw.SubHardware.SelectMany(WithSubHardware).Prepend(hw);

    public static void UpdateAll(this IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.UpdateAll();
    }
}
