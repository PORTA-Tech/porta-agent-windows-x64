using System.Text.Json;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiIdentityLinkContractTests
{
    [Fact]
    public void SetIdentityLinkRequest_SerializesPinnedSnakeCaseFields()
    {
        var request = new LocalApiSetIdentityLinkRequest(
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1");

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(request));
        string[] propertyNames = json.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["account_id", "org_id"], propertyNames);
    }

    [Fact]
    public void IdentityLinkResponse_DeserializesPinnedSnakeCaseFields()
    {
        const string payload = """
            {
              "device_id": "6f390ec2-31b7-4f49-9c11-e508d477b633",
              "account_id": "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
              "org_id": "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
              "link_status": "linked",
              "linked_at_utc": "2026-03-11T10:00:00.000Z",
              "updated_at_utc": "2026-03-11T10:00:05.000Z"
            }
            """;

        LocalApiIdentityLinkResponse? response = JsonSerializer.Deserialize<LocalApiIdentityLinkResponse>(payload);

        Assert.NotNull(response);
        Assert.Equal("6f390ec2-31b7-4f49-9c11-e508d477b633", response!.DeviceId);
        Assert.Equal("2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b", response.AccountId);
        Assert.Equal("6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1", response.OrgId);
        Assert.Equal("linked", response.LinkStatus);
        Assert.Equal(DateTimeOffset.Parse("2026-03-11T10:00:00.000Z"), response.LinkedAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-03-11T10:00:05.000Z"), response.UpdatedAtUtc);
    }
}
