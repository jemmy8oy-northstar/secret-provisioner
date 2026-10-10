namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>A reply, as data. Serialising it is the host's job and happens in one place.</summary>
public interface IRouteReply
{
    int Status { get; }

    /// <summary>Serialised by the host. An error body is assembled from a fixed string, never from an exception.</summary>
    object Body { get; }

    /// <summary>The <c>Allow</c> header, set on a 405 only.</summary>
    string? Allow { get; }

    /// <summary>
    /// The handler's exception on a 500, handed to the caller to log where redaction is that
    /// layer's job. Refusing to echo it is not swallowing it.
    /// </summary>
    Exception? Thrown { get; }
}
