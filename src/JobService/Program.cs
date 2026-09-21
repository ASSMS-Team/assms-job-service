using System.Reflection;
using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;

using JobService.Messaging.Consumers;
using JobService.Messaging.Producers;
using JobService.Repositories;
using JobService.Security;
using JobService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("default")
    ?? throw new InvalidOperationException("Connection string 'default' was not found.");

// Both of these are addresses of things this service talks to but does not own,
// so neither may be hard-coded - staging and production point elsewhere.
var customerServiceBaseUrl = builder.Configuration["CustomerService:BaseUrl"]
    ?? throw new InvalidOperationException("Configuration value 'CustomerService:BaseUrl' was not found.");
var customerServiceInternalKey = builder.Configuration["CustomerService:InternalServiceKey"]
    ?? throw new InvalidOperationException("Configuration value 'CustomerService:InternalServiceKey' was not found.");

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

// Job list and detail data is staff-only. Use the same issuer, audience and
// signing key as Customer & Asset and Dispatch so one staff login is valid
// across the services without forwarding a password or making a cross-service
// authorization call.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(options => Encoding.UTF8.GetByteCount(options.SigningKey) >= 32,
        "Authentication:Jwt:SigningKey must contain at least 32 UTF-8 bytes.")
    .ValidateOnStart();
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.UniqueName,
            RoleClaimType = "role",
        };
    });
builder.Services.AddAuthorization();

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
builder.Services.AddScoped<JobMigrationRunner>();
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
    client.DefaultRequestHeaders.Add("X-ASSMS-Service-Key", customerServiceInternalKey);
});

// Singleton: building a producer opens sockets and starts a background thread,
// so one per request would be ruinous, and IProducer is thread-safe by design.
builder.Services.AddSingleton<IEventPublisher>(serviceProvider =>
    new KafkaEventPublisher(
        kafkaBootstrapServers,
        serviceProvider.GetRequiredService<ILogger<KafkaEventPublisher>>()));

// This service both produces and consumes: it publishes JobCreated when a job is
// raised, and reads back the JobAssigned that Dispatch decides in response.
//
// A hosted service is a singleton, which is why it takes the scope factory and
// not the repository - see the note in JobAssignedConsumer.
builder.Services.AddHostedService(serviceProvider =>
    new JobAssignedConsumer(
        kafkaBootstrapServers,
        serviceProvider.GetRequiredService<IServiceScopeFactory>(),
        serviceProvider.GetRequiredService<ILogger<JobAssignedConsumer>>()));

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
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter the JWT returned by POST /api/auth/login.",
    });
    options.OperationFilter<AuthorizeOperationFilter>();
});

var app = builder.Build();

// Applied as its own deployment step rather than on startup, matching Dispatch
// and Reporting: the CD workflow runs the image once with this flag, waits for it
// to exit, and only then replaces the running container. Migrating on startup
// would let several instances race the same ALTER, and would leave a consumer
// running against a half-migrated schema if one failed.
if (args.Contains("--apply-migrations", StringComparer.Ordinal))
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<JobMigrationRunner>().ApplyAsync();
    return;
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();

app.UseHttpsRedirection();

app.UseCors(FrontendCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

partial class Program
{
    private const string FrontendCorsPolicy = "Frontend";
}
