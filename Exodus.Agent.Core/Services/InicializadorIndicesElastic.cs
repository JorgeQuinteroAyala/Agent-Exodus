using Exodus.Agent.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nest;

namespace Exodus.Agent.Core.Services;

public class InicializadorIndicesElastic
{
    private readonly IElasticClient Elastic;
    private readonly IConfiguration Config;
    private readonly ILogger<InicializadorIndicesElastic> Logger;

    public InicializadorIndicesElastic(IElasticClient pElastic, IConfiguration pConfig,
        ILogger<InicializadorIndicesElastic> pLogger)
    {
        Elastic = pElastic;
        Config = pConfig;
        Logger = pLogger;
    }

    public async Task AsegurarIndicesAsync(CancellationToken pCancelacionToken)
    {
        var IndiceServicios = Config["Elastic:ServicesIndex"]!;
        var IndiceLatidos = Config["Elastic:HeartbeatsIndex"]!;

        await CrearIndiceServicios(IndiceServicios, pCancelacionToken);
        await CrearIndiceHeartBeats(IndiceLatidos, pCancelacionToken);
    }

    private async Task CrearIndiceServicios(string pNombreIndice, CancellationToken pCancelacionToken)
    {
        var Existe = await Elastic.Indices.ExistsAsync(pNombreIndice, d => d, pCancelacionToken);

        if (Existe.Exists)
        {
            Logger.LogInformation("El índice {Indice} ya existe.", pNombreIndice);
            return;
        }

        Logger.LogInformation("Creando índice {Indice}...", pNombreIndice);

        await Elastic.Indices.CreateAsync(pNombreIndice, c => c
            .Settings(s => s.NumberOfShards(1).NumberOfReplicas(1))
            .Map<EstadoServicio>(m => m
                .AutoMap()
                .Properties(ps => ps
                    .Keyword(k => k.Name(n => n.IdServicio))
                    .Keyword(k => k.Name(n => n.Nombre))
                    .Keyword(k => k.Name(n => n.SistemaOperativo))
                    .Keyword(k => k.Name(n => n.Criticidad))
                    .Keyword(k => k.Name(n => n.Estado))
                    .Keyword(k => k.Name(n => n.Salud))
                    .Boolean(b => b.Name(n => n.TieneHealth))
                    .Keyword(k => k.Name(n => n.UrlHealth))
                    .Number(n => n.Name(n2 => n2.Replicas).Type(NumberType.Integer))
                    .Date(d => d.Name(n => n.UltimaRevision))
                    .Date(d => d.Name(n => n.UltimaActualizacion))
                    .Keyword(k => k.Name(n => n.NombreHost))
                    .Keyword(k => k.Name(n => n.UrlsProbadas))
                    .Keyword(k => k.Name(n => n.UrlExitosa))
                    .Keyword(k => k.Name(n => n.SaludDetalle))
                    .Keyword(k => k.Name(n => n.SaludInterna))
                    .Keyword(k => k.Name(n => n.SaludDetalleInterna))
                    .Keyword(k => k.Name(n => n.UrlExitosaInterna))
                    .Keyword(k => k.Name(n => n.SaludExterna))
                    .Keyword(k => k.Name(n => n.SaludDetalleExterna))
                    .Keyword(k => k.Name(n => n.UrlExitosaExterna))
                    .Keyword(k => k.Name(h => h.NodoHostname))
                    .Number(nu => nu.Name(h => h.CpuPorcentaje).Type(NumberType.Double))
                    .Number(nu => nu.Name(h => h.MemoriaPorcentaje).Type(NumberType.Double))
                    .Nested<HealthCheck>(n => n
                        .Name(n2 => n2.HealthChecks)
                        .Properties(h => h
                            .Date(d => d.Name(x => x.TsUtc))
                            .Keyword(k => k.Name(x => x.Salud))
                            .Keyword(k => k.Name(x => x.UrlExitosa))
                            .Keyword(k => k.Name(x => x.Detalle))
                            .Keyword(k => k.Name(x => x.Estado))
                            .Keyword(k => k.Name(x => x.HostsRunning))
                            .Number(nu => nu.Name(x => x.ReplicasRunning).Type(NumberType.Integer))
                            .Number(nu => nu.Name(x => x.ReplicasDeseadas).Type(NumberType.Integer))
                            .Keyword(k => k.Name(x => x.SaludInterna))
                            .Keyword(k => k.Name(x => x.SaludDetalleInterna))
                            .Keyword(k => k.Name(x => x.UrlExitosaInterna))
                            .Keyword(k => k.Name(x => x.SaludExterna))
                            .Keyword(k => k.Name(x => x.SaludDetalleExterna))
                            .Keyword(k => k.Name(x => x.UrlExitosaExterna))
                            .Object<MetricasRecursos>(r => r
                                .Name(n3 => n3.Recursos)
                                .Properties(rp => rp
                                    .Number(nu => nu.Name(x => x.CpuPorcentajeMin).Type(NumberType.Double))
                                    .Number(nu => nu.Name(x => x.CpuPorcentajeAvg).Type(NumberType.Double))
                                    .Number(nu => nu.Name(x => x.CpuPorcentajeMax).Type(NumberType.Double))
                                    .Number(nu => nu.Name(x => x.CpuLimiteNucleos).Type(NumberType.Double))
                                    .Number(nu => nu.Name(x => x.MemoriaBytesMax).Type(NumberType.Long))
                                    .Number(nu => nu.Name(x => x.MemoriaLimiteBytes).Type(NumberType.Long))
                                    .Number(nu => nu.Name(x => x.MemoriaPorcentajeMin).Type(NumberType.Double))
                                    .Number(nu => nu.Name(x => x.MemoriaPorcentajeAvg).Type(NumberType.Double))
                                    .Number(nu => nu.Name(x => x.MemoriaPorcentajeMax).Type(NumberType.Double))
                                    .Boolean(b => b.Name(x => x.AppPoolCompartido))
                                    .Keyword(k => k.Name(x => x.OrigenMetrica))
                                )
                            )
                            .Nested<ResultadoPruebaUrl>(rp => rp
                                .Name(x => x.PruebasUrls)
                                .Properties(rpp => rpp
                                    .Keyword(k => k.Name(r => r.Url))
                                    .Keyword(k => k.Name(r => r.Tipo))
                                    .Boolean(b => b.Name(r => r.Alcanzable))
                                    .Number(nu => nu.Name(r => r.CodigoHttp).Type(NumberType.Integer))
                                    .Number(nu => nu.Name(r => r.TiempoRespuestaMs).Type(NumberType.Long))
                                    .Keyword(k => k.Name(r => r.Detalle))
                                )
                            )
                        )
                    )
                )
            ), pCancelacionToken);
    }

    private async Task CrearIndiceHeartBeats(string pNombreIndice, CancellationToken pCancelacionToken)
    {
        var Existe = await Elastic.Indices.ExistsAsync(pNombreIndice, d => d, pCancelacionToken);

        if (Existe.Exists)
        {
            Logger.LogInformation("El índice {Indice} ya existe.", pNombreIndice);
            return;
        }

        Logger.LogInformation("Creando índice {Indice}...", pNombreIndice);

        await Elastic.Indices.CreateAsync(pNombreIndice, c => c
            .Settings(s => s.NumberOfShards(1).NumberOfReplicas(1))
            .Map<HeartBeat>(m => m
                .AutoMap()
                .Properties(ps => ps
                    .Keyword(k => k.Name(h => h.TipoDocumento))
                    .Keyword(k => k.Name(h => h.IdAgente))
                    .Keyword(k => k.Name(h => h.Estado))
                    .Keyword(k => k.Name(h => h.NombreHost))
                    .Keyword(k => k.Name(h => h.SistemaOperativo))
                    .Date(d => d.Name(h => h.UltimoLatido))
                )
            ), pCancelacionToken);
    }
}
