// Telemetry string splitter for x:Bind.
//
// The ViewModel hands the UI one already-formatted string ("83°C", "4109 RPM", "N/A").
// UI-DESIGN-RULES §3.3 wants the number at 14 SemiBold Primary and the unit at 11 Regular
// Muted, so the tuning status card needs the two halves separately. Doing that here keeps
// the ViewModel untouched (the hardware/VM path is out of scope) and keeps the split
// rule — trailing letters / ° / % are the unit — in one place.
//
// Used as: {x:Bind controls:TelemetryText.Value(Vm.CpuFanText), Mode=OneWay}
// The binding re-evaluates whenever the ViewModel raises PropertyChanged for that path.

namespace Predex.WinUI.Controls
{
    /// <summary>Splits "4109 RPM" into "4109" + "RPM" for two-weight rendering.</summary>
    public static class TelemetryText
    {
        /// <summary>Missing reading. The design rules want an em dash, not "N/A".</summary>
        private const string Placeholder = "—";

        /// <summary>The numeric part of a telemetry string ("4109", "83", "—").</summary>
        public static string Value(string? text) => Split(text).Value;

        /// <summary>The unit part of a telemetry string ("RPM", "°C", "").</summary>
        public static string Unit(string? text) => Split(text).Unit;

        private static (string Value, string Unit) Split(string? text)
        {
            string raw = (text ?? string.Empty).Trim();
            if (raw.Length == 0) return (Placeholder, string.Empty);
            if (raw == "N/A") return (Placeholder, string.Empty);

            // Walk back over the unit: letters, the degree sign, percent, per-slash.
            int cut = raw.Length;
            while (cut > 0)
            {
                char c = raw[cut - 1];
                if (!char.IsLetter(c) && c != '°' && c != '%' && c != '/') break;
                cut--;
            }

            // Nothing numeric in front of the unit: show the whole thing as the value.
            string value = raw[..cut].Trim();
            if (value.Length == 0) return (raw, string.Empty);

            return (value, raw[cut..].Trim());
        }
    }
}
