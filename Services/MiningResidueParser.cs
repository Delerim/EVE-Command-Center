using System.Globalization;
using System.Text.RegularExpressions;

namespace EveCommandCenter.Services;

// Residue lines contain units but no ore identity. Keep them out of yield,
// market value, critical-cycle and activity calculations.
internal static class MiningResidueParser
{
    internal static bool TryParseAmount(string line, out int amount)
    {
        amount = 0;
        if (!line.Contains("(mining)", StringComparison.Ordinal) ||
            !AlertPatterns.Matches(line, "mining_residue")) return false;
        string clean = Regex.Replace(line, @"<[^>]+>", "");
        clean = Regex.Replace(clean, @"^\[\s*[^\]]+\]\s*\(mining\)\s*", "");
        var numbers = Regex.Matches(clean, @"[0-9]+(?:[.,\s  ][0-9]+)*");
        if (numbers.Count != 1) return false;
        string digits = Regex.Replace(numbers[0].Value, @"[^0-9]", "");
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount > 0;
    }
}
