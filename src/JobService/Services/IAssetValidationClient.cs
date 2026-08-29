namespace JobService.Services;

// The answer to "may a job be raised against this asset for this customer?".
// Every value except Valid is a business refusal the Agent can act on, which
// is why the service being unreachable is not one of them - see
// AssetValidationUnavailableException below.
public enum AssetValidationOutcome
{
    Valid = 0,
    AssetNotFound = 1,
    // The asset exists but is registered to a different customer. Told apart
    // from AssetNotFound because the two are different corrections: one is the
    // wrong asset id, the other is the wrong pairing.
    CustomerMismatch = 2,
    AssetInactive = 3,
    CustomerInactive = 4
}

// Raised when the Customer & Asset Service could not be reached or answered
// with a server error. Thrown rather than returned as an outcome, and the
// distinction is the point: a 404 is the customer service telling us the asset
// is not there, which is an answer, whereas this is no answer at all. Treating
// it as a validation failure would reject a request that might be perfectly
// valid, so it travels as an exception and the controller renders it 503.
public class AssetValidationUnavailableException : Exception
{
    public AssetValidationUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IAssetValidationClient
{
    // Throws AssetValidationUnavailableException when the customer service
    // cannot be reached or fails; every other result is an AssetValidationOutcome.
    Task<AssetValidationOutcome> ValidateAsync(string assetId, string customerId);
}
