using System.Reflection;
using JobService.Messaging.Contracts;
using JobService.Security;

namespace JobService.Tests;

// Requests, responses, models and event contracts created without a value for
// a non-nullable string must hold "", never a placeholder, so validation,
// mapping and serialisation see a real empty value.
public class DefaultValuesTests
{
    private static readonly string[] Namespaces =
        ["JobService.DTOs", "JobService.Models", "JobService.Messaging.Contracts"];

    public static IEnumerable<object[]> Types() =>
        typeof(Services.JobService).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true, IsGenericTypeDefinition: false }
                        && Namespaces.Contains(t.Namespace)
                        && t.GetConstructor(Type.EmptyTypes) is not null
                        && !t.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute"))
            .Select(t => new object[] { t.FullName! });

    [Theory]
    [MemberData(nameof(Types))]
    public void NonNullableStringProperties_DefaultToEmpty(string typeName) =>
        AssertStringDefaults(typeof(Services.JobService).Assembly.GetType(typeName)!);

    [Fact]
    public void EventEnvelope_StringPropertiesDefaultToEmpty() =>
        AssertStringDefaults(typeof(EventEnvelope<JobCreatedPayload>));

    [Fact]
    public void JwtOptions_DefaultToTheSharedIssuerAndAudience_AndNoKey()
    {
        var options = new JwtOptions();

        Assert.Equal("assms-customer-asset-service", options.Issuer);
        Assert.Equal("assms-internal", options.Audience);
        Assert.Equal(string.Empty, options.SigningKey);
    }

    private static void AssertStringDefaults(Type type)
    {
        var instance = Activator.CreateInstance(type)!;
        var nullability = new NullabilityInfoContext();
        foreach (var property in type.GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanRead))
        {
            var value = (string?)property.GetValue(instance);
            if (nullability.Create(property).ReadState == NullabilityState.NotNull)
                Assert.True(value == string.Empty, $"{type.Name}.{property.Name} should default to an empty string but was '{value}'.");
            else
                Assert.Null(value);
        }
    }
}
