using System.Net;
using System.Text.Json;

namespace JobService.Services;

// Asks the Customer & Asset Service whether an asset may have a job raised
// against it. This service owns no copy of customerdb, so the question can only
// be answered over HTTP.
public class AssetValidationClient : IAssetValidationClient
{
    private const string ActiveStatus = "ACTIVE";

    // The customer service serializes camelCase; this is what reads it back.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public AssetValidationClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AssetValidationOutcome> ValidateAsync(string assetId, string customerId)
    {
        var asset = await GetAsync<AssetView>($"api/assets/{assetId}");

        // A 404 here is an answer, not a failure: the customer service looked
        // and there is no such asset.
        if (asset is null)
        {
            return AssetValidationOutcome.AssetNotFound;
        }

        // Checked before status, because raising a job against someone else's
        // equipment is the wrong asset entirely - whether that asset happens to
        // be active is beside the point.
        if (!string.Equals(asset.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
        {
            return AssetValidationOutcome.CustomerMismatch;
        }

        if (!string.Equals(asset.Status, ActiveStatus, StringComparison.Ordinal))
        {
            return AssetValidationOutcome.AssetInactive;
        }

        // A second call, because the asset representation carries the owner's
        // id but not the owner's status, and an active asset can belong to a
        // customer who has since been deactivated.
        var customer = await GetAsync<CustomerView>($"api/customers/{customerId}");

        if (customer is null)
        {
            // The asset says it belongs to this customer and the customer does
            // not exist. Customers are deactivated rather than deleted, so this
            // is a data anomaly rather than a state a request can reach - it is
            // reported as a mismatch because the pairing the caller asserted
            // could not be confirmed, which is the closest true statement.
            return AssetValidationOutcome.CustomerMismatch;
        }

        if (!string.Equals(customer.Status, ActiveStatus, StringComparison.Ordinal))
        {
            return AssetValidationOutcome.CustomerInactive;
        }

        return AssetValidationOutcome.Valid;
    }

    // Returns null for 404 and throws for everything else that is not a 200.
    // That split is the whole contract of this method: 404 is the customer
    // service answering, and a 500, a timeout or a refused connection is it
    // failing to.
    private async Task<T?> GetAsync<T>(string path)
        where T : class
    {
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.GetAsync(path);
        }
        catch (HttpRequestException ex)
        {
            // Refused connection, DNS failure, TLS failure.
            throw new AssetValidationUnavailableException(
                "The Customer & Asset Service could not be reached.", ex);
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient surfaces its own timeout as a cancellation, and no
            // caller here passes a cancellation token, so this is a timeout.
            throw new AssetValidationUnavailableException(
                "The Customer & Asset Service did not respond in time.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AssetValidationUnavailableException(
                    $"The Customer & Asset Service returned {(int)response.StatusCode} for {path}.");
            }

            var body = await response.Content.ReadAsStringAsync();

            try
            {
                return JsonSerializer.Deserialize<T>(body, SerializerOptions);
            }
            catch (JsonException ex)
            {
                // A 200 that is not the shape we expect is the service
                // malfunctioning, not the asset being invalid.
                throw new AssetValidationUnavailableException(
                    $"The Customer & Asset Service returned an unreadable body for {path}.", ex);
            }
        }
    }

    // Only the fields this validation reads. The customer service returns more;
    // binding the rest would tie this client to columns it does not care about.
    private sealed class AssetView
    {
        public string CustomerId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }

    private sealed class CustomerView
    {
        public string Status { get; set; } = string.Empty;
    }
}
