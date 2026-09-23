namespace ContextWindow;

public enum Dimension
{
    Booking,
    Shipment,
    Urgency,
    PickupFlexibility,
    Approval
}

public enum Sufficiency
{
    Sufficient,
    Missing,
    Contradictory
}

public sealed record Rating(int? Level, Sufficiency Evidence, double[]? Probabilities = null)
{
    public double? ExpectedLevel => Probabilities is null ? Level : Probabilities[1] + 2 * Probabilities[2];

    public void Validate()
    {
        if (Evidence != Sufficiency.Sufficient)
        {
            if (Level is not null || Probabilities is not null)
            {
                throw new FormatException("Missing or contradictory evidence cannot have a level.");
            }

            return;
        }

        if (Level is < 0 or > 2 || (Level is null && Probabilities is null))
        {
            throw new FormatException("A sufficient rating needs a level from zero to two.");
        }

        if (Probabilities is not null && (Probabilities.Length != 3 || Probabilities.Any(value => !double.IsFinite(value) || value < 0 || value > 1) || Math.Abs(Probabilities.Sum() - 1) > 0.0001))
        {
            throw new FormatException("Probabilities must be finite, bounded and sum to one.");
        }

        if (Probabilities is not null && Level is not null && Level != Array.IndexOf(Probabilities, Probabilities.Max()))
        {
            throw new FormatException("A level paired with probabilities must be their argmax.");
        }
    }
}

public sealed record Assessment(IReadOnlyDictionary<Dimension, Rating> Ratings)
{
    public Rating this[Dimension dimension] => Ratings[dimension];

    public void Validate()
    {
        if (Ratings.Count != 5 || Enum.GetValues<Dimension>().Any(dimension => !Ratings.ContainsKey(dimension)))
        {
            throw new FormatException("Every independent rubric must be present exactly once.");
        }

        foreach (Rating rating in Ratings.Values)
        {
            rating.Validate();
        }
    }
}

public enum OfferFeasibility
{
    Feasible,
    Blocked,
    Unresolved
}

public sealed record Offer(string Truck, decimal Revenue, decimal Contribution, decimal EmptyMilesCost, bool IsAlternative, bool EquipmentCompatible, OfferFeasibility Feasibility, string EmptyMilePositioning, string? ScheduleConflict = null, string? CompetingShipmentId = null);

public sealed record InquiryContext(IReadOnlyList<string> Conversation, string CustomerHistory, string? Pickup, string? Delivery, string? Cargo, IReadOnlyList<string> MissingInformation);

public sealed record Inquiry(string Id, string Message, string Split, IReadOnlyList<Offer> Offers, Assessment Reference, bool Blocked, bool Review, bool IndependentReference, InquiryContext Context);

public sealed record PriorityResult(string InquiryId, string? Truck, decimal? Score, string Status, string Explanation);

public sealed record PriorityWeights(decimal Contribution = 0.55m, decimal Shipment = 0.20m, decimal Urgency = 0.15m, decimal Flexibility = 0.10m);
