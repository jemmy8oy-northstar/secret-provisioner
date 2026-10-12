namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The planner was handed an observation that does not say what it looked at. Refused outright
/// rather than read charitably: an unlisted namespace read as empty is how a live Secret gets
/// overwritten.
/// </summary>
public sealed class InvalidObservationException : AppException
{
    public InvalidObservationException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "invalid_observation";
}
