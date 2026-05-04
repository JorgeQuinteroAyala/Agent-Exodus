using Exodus.Agent.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nest;

namespace Exodus.Agent.Core.Configuration;

public static class ExtensionesServicios
{
    public static IServiceCollection AgregarExodusCore(this IServiceCollection pServicios)
    {
        pServicios.AddSingleton<IElasticClient>(sp =>
        {
            var Config = sp.GetRequiredService<IConfiguration>();
            var Configuracion = new ConnectionSettings(new Uri(Config["Elastic:Url"]!))
                .DefaultIndex(Config["Elastic:ServicesIndex"])
                .DefaultFieldNameInferrer(p => p);

            return new ElasticClient(Configuracion);
        });

        pServicios.AddSingleton<EstadoEjecucionAgente>();
        pServicios.AddSingleton<InicializadorIndicesElastic>();
        pServicios.AddSingleton<ProveedorConfiguracionDinamica>();
        pServicios.AddSingleton<FiltrosComunes>();

        return pServicios;
    }
}
