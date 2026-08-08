namespace App.Core.Tools;

/// <summary>How chat turns choose which tool modules to attach.</summary>
public enum ToolRoutingMode
{
    /// <summary>Keyword / session heuristics only (default, zero model cost).</summary>
    Rules = 0,

    /// <summary>Small model classifies modules; falls back to rules on failure.</summary>
    Ai = 1,

    /// <summary>Rules first; AI only when rules are weak / PureChat / ambiguous.</summary>
    Hybrid = 2
}
