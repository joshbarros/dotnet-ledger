using Ledger.Shared;
using Ledger.Shared.Contracts;
using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Accounts;

public static class AccountsModule
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Ledger") ?? throw new InvalidOperationException("Connection string 'Ledger' is not configured.");
        services.AddDbContext<AccountsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", AccountsDbContext.SchemaName)));
        services.AddScoped<AccountsHandler>();
        services.AddScoped<IAccountCatalog, AccountCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<AccountsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<AccountsDbContext>>("funds-db", tags: ["ready"]);

        messaging.AddOutbox<AccountsDbContext>()
            .AddConsumer<AccountsHandler>(Topology.AccountsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
