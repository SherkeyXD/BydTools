namespace BydTools.Utils.VGMToolbox;

/// <summary>
/// Extended offset info that supports variable-based calculations.
/// </summary>
public class CalculatingOffsetInfo : OffsetInfo
{
    /// <summary>Variable placeholder string used in calculations.</summary>
    public const string VariablePlaceholder = "$V";

    /// <summary>Calculation expression string.</summary>
    public string Calculation { get; set; } = string.Empty;
}
