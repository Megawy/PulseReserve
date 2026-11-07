using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseReserve.Modules.Identity.Domain;
using PulseReserve.Modules.Identity.Features;
using PulseReserve.Modules.Identity.Persistence;
using PulseReserve.Modules.Identity.Persistence.Postgres;

namespace PulseReserve.Modules.Identity;

public static class IdentityModuleExtensions
{
    /// <summary>Registers the Identity module (own "identity" schema in the shared database).</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<IdentityDbContext>(db => db.UseNpgsql(connectionString,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema)));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<IUserStore, UserStore>();
        services.AddScoped<IdentityService>();
        return services;
    }
}
