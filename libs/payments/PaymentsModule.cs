using Ledger.Shared;
using Ledger.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Payments;

public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Ledger") ?? throw new InvalidOperationException("Connection string 'Ledger' is not configured.");
        services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", PaymentsDbContext.SchemaName)));
        services.Configure<PaymentOptions>(config.GetSection("Payments"));
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<PaymentsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<PaymentsDbContext>>("payments-db", tags: ["ready"]);

        messaging.AddOutbox<PaymentsDbContext>()
            .AddConsumer<PaymentsHandler>(Topology.PaymentsQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
