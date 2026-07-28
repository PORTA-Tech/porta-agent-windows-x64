using Porta.Agent.Core.Security;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Every test here passes an explicit path override. Without it these would write to
/// C:\ProgramData\PORTA\secrets and clobber the installed agent's real credential.
/// </summary>
public sealed class DeviceCredentialSecretsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "porta-device-credential-tests",
        Guid.NewGuid().ToString("N"));

    private string CredentialPath => Path.Combine(_directory, "device_credential.bin");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void ReadDeviceCredential_ReportsMissingRatherThanThrowing()
    {
        Secrets.DeviceCredentialReadResult result = Secrets.ReadDeviceCredential(CredentialPath);

        Assert.False(result.Success);
        Assert.Equal("DEVICE_CREDENTIAL_MISSING", result.ErrorCode);
        Assert.Null(result.DeviceSecret);
    }

    [Fact]
    public void WriteThenRead_RoundTripsTheCredential()
    {
        const string deviceId = "d93d90c1-a120-4c72-89ee-5c7d16efea31";
        const string secret = "PhaBLXLoPY2y3nWyKzP-3AKQ7c1bPqU1nQ9m7bYw1sE";

        Assert.True(Secrets.TryWriteDeviceCredential(deviceId, secret, CredentialPath));

        Secrets.DeviceCredentialReadResult result = Secrets.ReadDeviceCredential(CredentialPath);
        Assert.True(result.Success);
        Assert.Equal(deviceId, result.DeviceId);
        Assert.Equal(secret, result.DeviceSecret);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void TheFileOnDiskDoesNotContainThePlaintextSecret()
    {
        const string secret = "PhaBLXLoPY2y3nWyKzP-3AKQ7c1bPqU1nQ9m7bYw1sE";
        Secrets.TryWriteDeviceCredential("d93d90c1-a120-4c72-89ee-5c7d16efea31", secret, CredentialPath);

        byte[] onDisk = File.ReadAllBytes(CredentialPath);
        string asText = System.Text.Encoding.UTF8.GetString(onDisk);

        Assert.DoesNotContain(secret, asText, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewriting_ReplacesThePreviousCredential()
    {
        Secrets.TryWriteDeviceCredential("device-1", "secret-one", CredentialPath);
        Secrets.TryWriteDeviceCredential("device-1", "secret-two", CredentialPath);

        Secrets.DeviceCredentialReadResult result = Secrets.ReadDeviceCredential(CredentialPath);
        Assert.Equal("secret-two", result.DeviceSecret);
    }

    [Fact]
    public void EmptyInputIsRejectedRatherThanStored()
    {
        Assert.False(Secrets.TryWriteDeviceCredential("", "secret", CredentialPath));
        Assert.False(Secrets.TryWriteDeviceCredential("device-1", "  ", CredentialPath));
        Assert.False(File.Exists(CredentialPath));
    }

    [Fact]
    public void CorruptFileReportsAnErrorInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(CredentialPath, new byte[] { 1, 2, 3, 4, 5 });

        Secrets.DeviceCredentialReadResult result = Secrets.ReadDeviceCredential(CredentialPath);

        Assert.False(result.Success);
        Assert.Equal("DEVICE_CREDENTIAL_READ_FAILED", result.ErrorCode);
    }

    [Fact]
    public void DeleteRemovesTheCredential()
    {
        Secrets.TryWriteDeviceCredential("device-1", "secret-one", CredentialPath);
        Assert.True(File.Exists(CredentialPath));

        File.Delete(CredentialPath);

        Assert.False(Secrets.ReadDeviceCredential(CredentialPath).Success);
    }
}
