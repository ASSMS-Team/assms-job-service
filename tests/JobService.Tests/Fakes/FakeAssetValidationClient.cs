using JobService.Services;

namespace JobService.Tests;

// Stands in for the HTTP call to the Customer & Asset Service, so a service
// test can exercise every refusal without a second process listening.
public class FakeAssetValidationClient : IAssetValidationClient
{
    // Defaults to Valid so the happy path needs no configuration; the failure
    // tests set it to the outcome they are about.
    public AssetValidationOutcome OutcomeToReturn = AssetValidationOutcome.Valid;

    // Left null for every case that has an answer. Set it to
    // AssetValidationUnavailableException to stand in for the customer service
    // being unreachable, which the service deliberately does not catch.
    public Exception? ExceptionToThrow;

    public string? ValidatedAssetId;
    public string? ValidatedCustomerId;
    public int ValidateAsyncCallCount;

    public Task<AssetValidationOutcome> ValidateAsync(string assetId, string customerId)
    {
        // Recorded and counted before the throw, so a test that configures one
        // can still assert on what the call was given.
        ValidatedAssetId = assetId;
        ValidatedCustomerId = customerId;
        ValidateAsyncCallCount++;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(OutcomeToReturn);
    }
}
