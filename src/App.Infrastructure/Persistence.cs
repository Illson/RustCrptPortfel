using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Portfel.Application;

namespace Portfel.Infrastructure;

public static class Persistence
{
    public static IServiceCollection AddPortfel(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        bool demo = environment.IsDevelopment() && configuration.GetValue<bool>("Demo:Enabled");
        services.AddSingleton(new DemoSettings(demo));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<WorkspaceJobs>();
        string path = configuration["Storage:DatabasePath"] ?? Path.Combine(environment.ContentRootPath, ".portfel", "portfel.db");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 20 };
        services.AddDbContext<PortfelDb>(options => options.UseSqlite(connection.ToString()));
        services.AddScoped<IWorkspaceStore, SqliteWorkspaceStore>();
        services.AddHostedService<DatabaseStartup>();
        return services;
    }
}
