using Balenthiran.SecretProvisioner.WebApi;

ProvisionerServer.Build(args, ProvisionerSettings.FromEnvironment(Environment.GetEnvironmentVariable)).Run();

// Exposed so a test can name the entry assembly (top-level statements make Program internal).
public partial class Program;
