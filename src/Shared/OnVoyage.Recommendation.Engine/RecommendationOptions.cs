namespace OnVoyage.Recommendation.Engine;

/// <summary>Weights of §6.6 (defaults of annexe E). <c>WeightsVersion</c> is reported with every ranking.</summary>
public sealed record RecommendationOptions
{
    public int WeightsVersion { get; init; } = 1;
    public double Interest { get; init; } = 0.30;
    public double Collaborative { get; init; } = 0.15;
    public double Importance { get; init; } = 0.15;
    public double Distance { get; init; } = 0.15;
    public double Quality { get; init; } = 0.10;
    public double Novelty { get; init; } = 0.05;
    public double Context { get; init; } = 0.10;
    /// <summary><c>w_creator</c> of §6.15.</summary>
    public double CreatorWeight { get; init; } = 0.10;
    public int ColdStartDepth { get; init; } = 5;
    public int CompatibilityCap { get; init; } = 98;
    public EthicalLevel Ethical { get; init; } = EthicalLevel.Balanced;

    public double CrowdWeight => Ethical switch { EthicalLevel.Off => 0d, EthicalLevel.Balanced => 0.10, _ => 0.25 };

    public double GemWeight => Ethical switch { EthicalLevel.Off => 0d, EthicalLevel.Balanced => 0.05, _ => 0.15 };

    public static double ScaleMeters(TravelMode mode) => mode switch
    {
        TravelMode.Walk => 800d,
        TravelMode.Bike => 3000d,
        _ => 15000d,
    };
}
