using System.Reflection;
using System.Net;
using System.Text.Json;

using JobService.Messaging.Producers;
using JobService.Repositories;
using JobService.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("default")
    ?? throw new InvalidOperationException("Connection string 'default' was not found.");

// Both of these are addresses of things this service talks to but does not own,
// so neither may be hard-coded - staging and production point elsewhere.
var customerServiceBaseUrl = builder.Configuration["CustomerService:BaseUrl"]
    ?? throw new InvalidOperationException("Configuration value 'CustomerService:BaseUrl' was not found.");

var kafkaBootstrapServers = builder.Configuration["Kafka:BootstrapServers"]
    ?? throw new InvalidOperationException("Configuration value 'Kafka:BootstrapServers' was not found.");

// Add services to the container.

// Empty rather than a fallback origin when the key is absent: a missing
// configuration should refuse every browser origin, not quietly allow one.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// Nginx reaches the loopback-published container through Docker's bridge
// gateway. Trust only that proxy address when consuming client and scheme
// headers; requests from arbitrary networks cannot supply forwarded headers.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Parse("172.17.0.1").MapToIPv6());
});

builder.Services.AddSingleton<IDbConnectionFactory>(new MySqlConnectionFactory(connectionString));
builder.Services.AddScoped<IJobRepository, JobRepository>();
// Qualified: the class shares its name with the root namespace, so the bare
// name would bind to the namespace and not compile.
builder.Services.AddScoped<JobService.Services.JobService>();

// AddHttpClient rather than a new HttpClient per call: it pools and recycles
// the underlying handlers, which is what stops long-running processes from
// exhausting sockets or pinning stale DNS.
builder.Services.AddHttpClient<IAssetValidationClient, AssetValidationClient>(client =>
{
    // The trailing slash matters: without it the last path segment of a base
    // address would be replaced by the relative path rather than extended.
    client.BaseAddress = new Uri(customerServiceBaseUrl.TrimEnd('/') + "/");
});

// Singleton: building a producer opens sockets and starts a background thread,
// so one per request would be ruinous, and IProducer is thread-safe by design.
builder.Services.AddSingleton<IEventPublisher>(serviceProvider =>
    new KafkaEventPublisher(
        kafkaBootstrapServers,
        serviceProvider.GetRequiredService<ILogger<KafkaEventPublisher>>()));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Property names are already camelCased; this does the same for
        // dictionary keys, which is what ValidationProblemDetails.Errors is.
        // Without it DataAnnotations returns "AssetId" while a hand-built
        // problem returns "assetId", and the frontend has two rules to follow.
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Built from the assembly name so a project rename does not silently drop
    // the descriptions; the file sits next to the DLL in the output folder.
    var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();

app.UseHttpsRedirection();

app.UseCors(FrontendCorsPolicy);

app.UseAuthorization();

app.MapControllers();

app.Run();

partial class Program
{
    private const string FrontendCorsPolicy = "Frontend";
}
