namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>One request as the <see cref="RecordingHandler"/> saw it.</summary>
internal sealed record RecordedRequest(string Url, string Accept, string UserAgent);
