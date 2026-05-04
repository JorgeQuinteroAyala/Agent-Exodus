using Exodus.Agent.Core.Models;
using System.Diagnostics;

namespace Exodus.Agent.Core.Services;

/// <summary>
/// Resultado estructurado de una verificación de salud sobre una colección de URLs.
/// </summary>
public record ResultadoVerificacion(
    string Salud,
    string? UrlExitosa,
    string? Detalle,
    List<ResultadoPruebaUrl> Pruebas);

/// <summary>
/// Resultado consolidado de una verificación dual (interna + externa).
/// </summary>
public record ResultadoVerificacionDual(
    string SaludGeneral,
    string SaludInterna,
    string? UrlExitosaInterna,
    string? DetalleInterno,
    string SaludExterna,
    string? UrlExitosaExterna,
    string? DetalleExterno,
    List<ResultadoPruebaUrl> PruebasUrls);

public static class VerificadorHealthHttp
{
    /// <summary>
    /// Prueba cada URL de la colección y retorna telemetría detallada.
    /// </summary>
    /// <param name="pUrls">URLs a probar en orden.</param>
    /// <param name="pCliente">HttpClient a usar.</param>
    /// <param name="pTipo">Etiqueta para la telemetría ("Interna" o "Externa").</param>
    /// <param name="pEsAceptable">Predicado para decidir qué código HTTP cuenta como éxito.</param>
    /// <param name="pCancelacionToken">Token de cancelación.</param>
    public static async Task<ResultadoVerificacion> VerificarAsync(
        IEnumerable<string> pUrls, HttpClient pCliente, string pTipo,
        Func<int, bool> pEsAceptable, CancellationToken pCancelacionToken)
    {
        var Pruebas = new List<ResultadoPruebaUrl>();
        string? UrlExitosa = null;

        foreach (var Url in pUrls)
        {
            var Resultado = await IntentarAsync(pCliente, Url, pTipo, pEsAceptable, pCancelacionToken);
            Pruebas.Add(Resultado);

            if (Resultado.Alcanzable && UrlExitosa is null)
                UrlExitosa = Url;
        }

        var Salud = UrlExitosa is not null ? "Alcanzable" : "No Alcanzable";
        var Detalle = UrlExitosa is null
            ? "Todas las URLs fallaron o no respondieron con un código aceptable."
            : null;

        return new ResultadoVerificacion(Salud, UrlExitosa, Detalle, Pruebas);
    }

    /// <summary>
    /// Verifica dual (interna + externa). Sólo prueba externa contra URLs HTTPS.
    /// </summary>
    public static async Task<ResultadoVerificacionDual> VerificarDualAsync(
        IEnumerable<string> pUrlsInternas, IEnumerable<string> pUrlsExternas,
        HttpClient pClienteInterno, HttpClient pClienteExterno,
        Func<int, bool> pEsAceptable, CancellationToken pCancelacionToken)
    {
        var ListaExterna = pUrlsExternas.ToList();

        var TareaInterna = VerificarAsync(pUrlsInternas, pClienteInterno, "Interna",
            pEsAceptable, pCancelacionToken);

        var TareaExterna = ListaExterna.Count > 0
            ? VerificarAsync(ListaExterna, pClienteExterno, "Externa", pEsAceptable, pCancelacionToken)
            : Task.FromResult(new ResultadoVerificacion("", null, null, new List<ResultadoPruebaUrl>()));

        await Task.WhenAll(TareaInterna, TareaExterna);

        var Interna = await TareaInterna;
        var Externa = await TareaExterna;

        var SaludGeneral = ListaExterna.Count == 0
            ? Interna.Salud
            : Interna.Salud == "Alcanzable" && Externa.Salud == "Alcanzable"
                ? "Alcanzable"
                : "No Alcanzable";

        var TodasPruebas = new List<ResultadoPruebaUrl>(Interna.Pruebas.Count + Externa.Pruebas.Count);
        TodasPruebas.AddRange(Interna.Pruebas);
        TodasPruebas.AddRange(Externa.Pruebas);

        return new ResultadoVerificacionDual(
            SaludGeneral,
            Interna.Salud, Interna.UrlExitosa, Interna.Detalle,
            Externa.Salud, Externa.UrlExitosa, Externa.Detalle,
            TodasPruebas);
    }

    private static async Task<ResultadoPruebaUrl> IntentarAsync(
        HttpClient pCliente, string pUrl, string pTipo,
        Func<int, bool> pEsAceptable, CancellationToken pCancelacionToken)
    {
        var Cronometro = Stopwatch.StartNew();

        try
        {
            var Respuesta = await pCliente.GetAsync(pUrl, pCancelacionToken);
            Cronometro.Stop();

            var Codigo = (int)Respuesta.StatusCode;
            var EsOk = pEsAceptable(Codigo);

            return new ResultadoPruebaUrl
            {
                Url = pUrl,
                Tipo = pTipo,
                Alcanzable = EsOk,
                CodigoHttp = Codigo,
                TiempoRespuestaMs = Cronometro.ElapsedMilliseconds,
                Detalle = EsOk ? null : $"HTTP {Codigo}"
            };
        }
        catch (TaskCanceledException)
        {
            Cronometro.Stop();
            return new ResultadoPruebaUrl
            {
                Url = pUrl, Tipo = pTipo, Alcanzable = false, CodigoHttp = null,
                TiempoRespuestaMs = Cronometro.ElapsedMilliseconds, Detalle = "Timeout"
            };
        }
        catch (Exception Ex)
        {
            Cronometro.Stop();
            return new ResultadoPruebaUrl
            {
                Url = pUrl, Tipo = pTipo, Alcanzable = false, CodigoHttp = null,
                TiempoRespuestaMs = Cronometro.ElapsedMilliseconds, Detalle = Ex.Message
            };
        }
    }
}
