using System.Text;

namespace DlcBuilder.Modules.FeLang;

/// DJB2-style hash used by the front-end language pack to look up HAL strings
/// by id. Distinct from the lookup8 hash used for vault keys — DJB2 has a
/// `(uint)-1` seed (= `0xFFFFFFFF`) and a `((h &lt;&lt; 5) + h) + c` step. Hash
/// matches the engine's HAL resolution at PPU bind time.
public static class LangHashDjb2
{
    public static uint Hash(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint h = uint.MaxValue;
        foreach (byte b in Encoding.ASCII.GetBytes(input))
            h = (h << 5) + h + b;
        return h;
    }
}
