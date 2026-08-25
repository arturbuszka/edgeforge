using System.Net;
using EdgeForge.Api;
using EdgeForge.Core;
using EdgeForge.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML.OnnxRuntime;

// EdgeForge.Api: the local REST surface and the live HUD's push channel.
//
// On the container. The working agreement says not to introduce dependency
// injection infrastructure that is not needed, and the previous web layer took
// that as far as it goes: it cleared the configuration sources, built the object
// graph by hand and registered nothing at all. Two things here genuinely cannot
// be built by hand - IHubContext, SignalR's push-from-outside-a-request handle,
// and IDbContextFactory, which is what gives correct concurrency between the
// background service that writes and the handlers that read. So the container
// holds five entries and nothing else; InspectionSession, the detector pair,
// FrameWriter and MealEventSink are all plain `new` inside PipelineHost, exactly
// as Demo and Ui build theirs.
//
// On video. There is none here. MediaMTX pulls the camera's RTSP stream and
// republishes it over WebRTC; this process serves counts and timestamps, which
// is why "only events leave the device, never frames" needs no exception for it.

var builder = WebApplication.CreateBuilder(args);

// The repository's own configuration is the authority, exactly as the previous
// web layer had it. Leaving the ASP.NET defaults in place would give a second
// way to set the port and the environment, and a setting with two sources is the
// first thing to go wrong.
builder.Configuration.Sources.Clear();

EdgeForgeOptions options;
try
{
    options = LoadOptions();
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException)
{
    Console.Error.WriteLine($"Invalid configuration: {ex.Message}");
    return 2;
}

builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Listen(IPAddress.Parse(options.Api.BindAddress), options.Api.Port));

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSignalR();

// Only needed while Vite serves the front-end on its own port. In production the
// bundle is served from this origin and CORS does not apply at all.
builder.Services.AddCors(cors => cors.AddPolicy("dev", policy => policy
    .WithOrigins(options.Api.DevOrigins)
    .AllowAnyHeader()
    .AllowCredentials()));

builder.Services.AddSingleton(options);
builder.Services.AddDbContextFactory<MealDbContext>(db =>
    db.UseSqlite($"Data Source={DatabasePath(options.Storage)}"));
builder.Services.AddSingleton<IMealStore, SqliteMealStore>();

// Registered twice on purpose: once so endpoints and the hub can resolve the
// same instance, once so the host runs it. AddHostedService<T> alone would
// create a second, separate pipeline.
builder.Services.AddSingleton<PipelineHost>();
builder.Services.AddHostedService(services => services.GetRequiredService<PipelineHost>());

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EdgeForge.Api");

// Migrate before anything can query. Migrations rather than EnsureCreated: the
// latter cannot alter a table that already exists, and per-cat attribution will
// need a column - at which point the choice would be between dropping the user's
// meal history and hand-writing ALTER TABLE.
try
{
    var factory = app.Services.GetRequiredService<IDbContextFactory<MealDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
catch (Exception ex) when (ex is DbUpdateException or SqliteException or IOException
                               or UnauthorizedAccessException)
{
    // Without a database there is nothing to serve range queries from, and
    // starting anyway would look healthy while /api/meals returned nothing.
    Console.Error.WriteLine($"Could not open the meal database: {ex.Message}");
    return 2;
}

app.UseCors("dev");

// The Vite build lands in wwwroot. Missing on a fresh clone, which is fine -
// the API is useful without a front-end, and `npm run build` fills it in.
app.UseDefaultFiles();
app.UseStaticFiles();

ApiEndpoints.Map(app);
app.MapHub<LiveHub>("/hub/live");

try
{
    var pipeline = app.Services.GetRequiredService<PipelineHost>();

    // Runs on every start rather than once, and needs no marker file: the unique
    // index on the meal's start instant makes it idempotent. That is what lets a
    // deleted or corrupted database heal itself by restarting.
    if (pipeline.Journal is { } journal)
    {
        var store = app.Services.GetRequiredService<IMealStore>();
        var imported = await JournalBackfill.RunAsync(journal, store);

        log.LogInformation(
            "Journal backfill: {Imported} meal(s) imported, {Total} stored in total.",
            imported,
            await store.CountAsync());
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException)
{
    // An unreadable history is a reason to start counting from now, not a reason
    // to refuse to run - the same posture MealEventSink.Restore takes.
    log.LogWarning(ex, "Journal backfill failed; continuing without it.");
}

log.LogInformation(
    "Listening on http://{Address}:{Port} - stream from {Stream}",
    options.Api.BindAddress,
    options.Api.Port,
    options.Api.StreamUrl);

try
{
    await app.RunAsync();
    return 0;
}
catch (OnnxRuntimeException ex)
{
    // A missing or unreadable model is a configuration failure, not a crash.
    Console.Error.WriteLine($"Model unavailable: {ex.Message}");
    return 2;
}

static string DatabasePath(StorageOptions storage) =>
    string.IsNullOrWhiteSpace(storage.DatabasePath)
        ? Path.Combine(AppContext.BaseDirectory, "meals.db")
        : Path.Combine(AppContext.BaseDirectory, storage.DatabasePath);

static EdgeForgeOptions LoadOptions()
{
    // The third copy of this in the repository, after Demo and Ui. Consolidating
    // them means either a package reference in EdgeForge.Core - which has zero
    // today, deliberately - or a fifth project. Both are worth doing; neither is
    // worth folding into this change, where it would be untestable noise beside
    // the parts that actually needed reviewing.
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        // Gitignored overrides: camera credentials and machine-specific paths
        // belong here, not in the tracked file above.
        .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("EDGEFORGE_")
        .Build();

    var options = configuration.Get<EdgeForgeOptions>() ?? new EdgeForgeOptions();

    if (options.Camera.Capacity < 1)
    {
        throw new InvalidOperationException(
            $"Camera:Capacity must be at least 1, got {options.Camera.Capacity}.");
    }

    options.Meals.Validate();
    options.Api.Validate();
    options.Storage.Validate();

    return options;
}
