namespace ContextWindow;

public static class Priority
{
    public static PriorityResult Rank(Inquiry inquiry, Assessment assessment, PriorityWeights weights)
    {
        assessment.Validate();

        if (weights.Contribution < 0 || weights.Shipment < 0 || weights.Urgency < 0 || weights.Flexibility < 0
            || weights.Contribution + weights.Shipment + weights.Urgency + weights.Flexibility != 1)
        {
            throw new ArgumentException("Nonnegative priority weights must sum to one.", nameof(weights));
        }

        if (inquiry.Blocked)
        {
            return new(inquiry.Id, null, null, "blocked", "Blocked by the authored operational rule.");
        }

        if (inquiry.Review || assessment.Ratings.Values.Any(rating => rating.Evidence != Sufficiency.Sufficient))
        {
            return new(inquiry.Id, null, null, "review", "Evidence is missing, contradictory, or requires manual review.");
        }

        if (assessment[Dimension.Booking].ExpectedLevel == 0 || assessment[Dimension.Approval].ExpectedLevel == 0)
        {
            return new(inquiry.Id, null, null, "review", "Booking or approval is not yet ready; no offer is ranked.");
        }

        Offer? bestOffer = null;
        decimal bestScore = decimal.MinValue;

        foreach (Offer offer in inquiry.Offers)
        {
            double flexibility = assessment[Dimension.PickupFlexibility].ExpectedLevel!.Value;

            if (offer.Feasibility != OfferFeasibility.Feasible || !offer.EquipmentCompatible
                || offer.IsAlternative && flexibility == 0)
            {
                continue;
            }

            decimal contribution = Math.Clamp(offer.Contribution / 2000m, 0, 1);
            decimal applicableWeight = weights.Contribution + weights.Shipment + weights.Urgency
                + (offer.IsAlternative ? weights.Flexibility : 0);
            if (applicableWeight == 0)
            {
                continue;
            }

            decimal score = 100m * (
                weights.Contribution * contribution
                + weights.Shipment * (decimal)(assessment[Dimension.Shipment].ExpectedLevel!.Value / 2)
                + weights.Urgency * (decimal)(assessment[Dimension.Urgency].ExpectedLevel!.Value / 2)
                + (offer.IsAlternative ? weights.Flexibility * (decimal)(flexibility / 2) : 0)) / applicableWeight;

            if (score > bestScore)
            {
                bestScore = score;
                bestOffer = offer;
            }
        }

        if (bestOffer is null)
        {
            return new(inquiry.Id, null, null, "review", "No eligible truck offer.");
        }

        return new(inquiry.Id, bestOffer.Truck, decimal.Round(bestScore, 2), "ranked",
            $"{bestOffer.Truck}: precomputed contribution {bestOffer.Contribution:C}; shipment {assessment[Dimension.Shipment].ExpectedLevel:0.##}/2; urgency {assessment[Dimension.Urgency].ExpectedLevel:0.##}/2. Human dispatch decision required.");
    }
}
