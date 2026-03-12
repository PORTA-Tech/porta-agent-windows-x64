using System.Text.Json;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

public sealed class DeviceIdentityFleetContractTests
{
    [Fact]
    public void DeviceRegistrationRequestPayload_UsesExpectedCamelCaseContract()
    {
        var payload = new DeviceRegistrationRequestPayload(
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            MachineName: "HP-LAPTOP",
            DeviceDisplayName: "HP Laptop",
            Platform: "windows",
            AgentVersion: "1.0.0",
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            DeviceCaptureConsentState: "accepted",
            DeviceCaptureConsentVersion: "1.0.0",
            DeviceCaptureConsentedAt: "2026-03-12T09:00:00.000Z",
            DeviceCaptureTelemetryLevel: "enhanced",
            SessionToken: "session-token",
            LocalProofToken: "proof-token");

        string json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"deviceId\"", json);
        Assert.Contains("\"machineName\"", json);
        Assert.Contains("\"deviceDisplayName\"", json);
        Assert.Contains("\"accountId\"", json);
        Assert.Contains("\"orgId\"", json);
        Assert.Contains("\"deviceCaptureConsentState\"", json);
        Assert.Contains("\"deviceCaptureConsentVersion\"", json);
        Assert.Contains("\"deviceCaptureConsentedAt\"", json);
        Assert.Contains("\"deviceCaptureTelemetryLevel\"", json);
        Assert.Contains("\"sessionToken\"", json);
        Assert.Contains("\"localProofToken\"", json);
    }

    [Fact]
    public void DeviceLinkResultPayload_DeserializesExpectedShape()
    {
        const string json = """
        {
          "ok": true,
          "linkState": "linked",
          "device": {
            "deviceId": "6f390ec2-31b7-4f49-9c11-e508d477b633",
            "orgId": "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            "machineName": "HP-LAPTOP",
            "deviceDisplayName": "HP Laptop",
            "platform": "windows",
            "agentVersion": "1.0.0",
            "status": "active",
            "firstSeenAt": "2026-03-11T09:00:00.000Z",
            "lastSeenAt": "2026-03-11T09:05:00.000Z",
            "currentAccountId": "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            "revokedAt": null,
            "createdAt": "2026-03-11T09:00:00.000Z",
            "updatedAt": "2026-03-11T09:05:00.000Z"
          }
        }
        """;

        DeviceLinkResultPayload? payload = JsonSerializer.Deserialize<DeviceLinkResultPayload>(json);

        Assert.NotNull(payload);
        Assert.True(payload!.Ok);
        Assert.Equal("linked", payload.LinkState);
        Assert.Equal("HP Laptop", payload.Device.DeviceDisplayName);
        Assert.Equal("2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b", payload.Device.CurrentAccountId);
    }
}
