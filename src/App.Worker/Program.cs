using Portfel.Application;
using Portfel.Infrastructure;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPortfel(builder.Configuration, builder.Environment);
builder.Services.AddHostedService<PortfolioWorker>();
await builder.Build().RunAsync();
