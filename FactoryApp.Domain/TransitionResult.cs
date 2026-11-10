using FactoryApp.Domain.Events;

namespace FactoryApp.Domain;

public enum TransitionOutcome { Applied, Invalid, NotFound }

public sealed record TransitionResult(TransitionOutcome Outcome, string? Error = null, BuildStatusChangedEvent? Event = null)
{
    public bool Succeeded => Outcome == TransitionOutcome.Applied;
}
