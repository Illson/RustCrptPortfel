using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Portfel.Application;

namespace Portfel.Infrastructure;

public static class Persistence
{
    public static IServiceCollection AddPortfel(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddLogging(logging => logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning));
        string? path = configuration["Storage:DatabasePath"];
        if (path is null && environment.IsDevelopment())
            path = Path.Combine(environment.ContentRootPath, ".portfel", "portfel.db");
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
            throw new InvalidOperationException("Storage:DatabasePath должен указывать на постоянный файл SQLite.");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 20, ForeignKeys = true };
        services.AddDbContext<PortfelDb>(options => options.UseSqlite(connection.ToString())
            // DatabaseStartup takes the SQLite writer lock before running EF migrations.
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MigrationsUserTransactionWarning)));
        services.AddScoped<IWorkspaceStore, SqliteWorkspaceStore>();
        services.AddHostedService<DatabaseStartup>();
        return services;
    }
}
