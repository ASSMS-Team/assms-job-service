using System.Net;
using System.Text;
using JobService.Services;

namespace JobService.Tests;

// The validation decisions AssetValidationClient makes from the Customer &
// Asset Service's camelCase JSON responses.
public class AssetValidationFlowTests
{
    private sealed class RoutedHandler(Dictionary<string, (HttpStatusCode Status, string Body)> routes) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var (status, body) = routes.TryGetValue(path, out var route) ? route : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (AssetValidationClient Client, RoutedHandler Handler) Client(string? asset, string? customer)
    {
        var routes = new Dictionary<string, (HttpStatusCode, string)>();
        if (asset is not null) routes["/api/assets/asset-1"] = (HttpStatusCode.OK, asset);
        if (customer is not null) routes["/api/customers/cust-1"] = (HttpStatusCode.OK, customer);
        var handler = new RoutedHandler(routes);
        return (new AssetValidationClient(new HttpClient(handler) { BaseAddress = new Uri("http://customer.local/") }), handler);
    }

    private const string ActiveAsset = """{"customerId":"CUST-1","status":"ACTIVE"}""";
    private const string ActiveCustomer = """{"status":"ACTIVE"}""";

    [Fact]
    public async Task Valid_WhenTheAssetBelongsToTheCustomer_AndBothAreActive()
    {
        var (client, handler) = Client(ActiveAsset, ActiveCustomer);

        Assert.Equal(AssetValidationOutcome.Valid, await client.ValidateAsync("asset-1", "cust-1"));
        Assert.Equal(new[] { "/api/assets/asset-1", "/api/customers/cust-1" }, handler.Paths);
    }

    [Fact]
    public async Task AssetNotFound_WhenTheAssetDoesNotExist_WithoutLookingUpTheCustomer()
    {
        var (client, handler) = Client(asset: null, ActiveCustomer);

        Assert.Equal(AssetValidationOutcome.AssetNotFound, await client.ValidateAsync("asset-1", "cust-1"));
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task CustomerMismatch_WhenTheAssetBelongsToAnotherCustomer()
    {
        var (client, handler) = Client("""{"customerId":"cust-2","status":"ACTIVE"}""", ActiveCustomer);

        Assert.Equal(AssetValidationOutcome.CustomerMismatch, await client.ValidateAsync("asset-1", "cust-1"));
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task AssetInactive_WhenTheAssetIsNotActive()
    {
        var (client, handler) = Client("""{"customerId":"cust-1","status":"INACTIVE"}""", ActiveCustomer);

        Assert.Equal(AssetValidationOutcome.AssetInactive, await client.ValidateAsync("asset-1", "cust-1"));
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task AssetStatusComparisonIsCaseSensitive()
    {
        var (client, _) = Client("""{"customerId":"cust-1","status":"active"}""", ActiveCustomer);

        Assert.Equal(AssetValidationOutcome.AssetInactive, await client.ValidateAsync("asset-1", "cust-1"));
    }

    [Fact]
    public async Task CustomerMismatch_WhenTheCustomerRecordIsMissing()
    {
        var (client, _) = Client(ActiveAsset, customer: null);

        Assert.Equal(AssetValidationOutcome.CustomerMismatch, await client.ValidateAsync("asset-1", "cust-1"));
    }

    [Fact]
    public async Task CustomerInactive_WhenTheCustomerIsNotActive()
    {
        var (client, _) = Client(ActiveAsset, """{"status":"INACTIVE"}""");

        Assert.Equal(AssetValidationOutcome.CustomerInactive, await client.ValidateAsync("asset-1", "cust-1"));
    }

    [Fact]
    public async Task AFieldlessAssetResponse_HasAnEmptyOwnerAndIsTreatedAsInactive()
    {
        var (client, _) = Client("{}", ActiveCustomer);

        Assert.Equal(AssetValidationOutcome.AssetInactive, await client.ValidateAsync("asset-1", ""));
    }

    [Fact]
    public async Task AFieldlessCustomerResponse_IsTreatedAsInactive()
    {
        var (client, _) = Client(ActiveAsset, "{}");

        Assert.Equal(AssetValidationOutcome.CustomerInactive, await client.ValidateAsync("asset-1", "cust-1"));
    }
}
