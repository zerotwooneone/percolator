using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Percolator.Grpc.Data;
using Percolator.Grpc.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ??
                       throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.AddGrpc();

//todo: move these registrations
builder.Services.AddSingleton<PersistenceService>();
builder.Services.AddSingleton<SelfEncryptionService>(s=>new SelfEncryptionService("6e3c367d-380c-4a0d-8b66-ad397fbac2d9"));
builder.Services.AddSingleton<HandshakeService>();
builder.Services.AddSingleton<BusyService>();

var app = builder.Build();

app.UseAuthorization();

// Configure the HTTP request pipeline.
app.MapGrpcService<GreeterService>();
app.MapGrpcService<StreamerService>();
app.MapGet("/",
    () =>
        "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();