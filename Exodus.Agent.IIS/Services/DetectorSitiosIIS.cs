using Exodus.Agent.IIS.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Web.Administration;
using System.Runtime.InteropServices;

namespace Exodus.Agent.IIS.Services;

public class DetectorSitiosIIS
{
    private readonly ILogger<DetectorSitiosIIS> Logger;

    public DetectorSitiosIIS(ILogger<DetectorSitiosIIS> pLogger)
    {
        Logger = pLogger;
    }

    public List<SitioIIS> ObtenerSitios()
    {
        const int MaxIntentos = 2;

        for (int Intento = 1; Intento <= MaxIntentos; Intento++)
        {
            try
            {
                return ObtenerSitiosInterno();
            }
            catch (COMException Ex) when (Intento < MaxIntentos)
            {
                Logger.LogWarning(Ex,
                    "COMException al leer sitios IIS (intento {Intento}/{Max}). Reintentando.", Intento, MaxIntentos);
                Thread.Sleep(250);
            }
            catch (Exception Ex)
            {
                Logger.LogError(Ex, "Error al leer sitios desde IIS.");
                return new List<SitioIIS>();
            }
        }

        return new List<SitioIIS>();
    }

    private List<SitioIIS> ObtenerSitiosInterno()
    {
        var Resultado = new List<SitioIIS>();
        using var Administrador = new ServerManager();

        foreach (var Sitio in Administrador.Sites)
        {
            ObjectState Estado;

            try
            {
                Estado = Sitio.State;
            }
            catch (COMException Ex)
            {
                Logger.LogDebug(Ex,
                    "No se pudo leer State del sitio {Sitio}; se omite este ciclo.",
                    Sitio.Name);
                continue;
            }

            if (Estado != ObjectState.Started)
            {
                Logger.LogInformation("Sitio omitido por estado {Estado}: {Nombre}", Estado, Sitio.Name);
                continue;
            }

            var UrlsLocales = new List<string>();
            var UrlsInternas = new List<string>();
            var UrlsExternas = new List<string>();

            foreach (var Binding in Sitio.Bindings)
            {
                var Protocolo = Binding.Protocol?.ToLowerInvariant();

                if (Protocolo != "http" && Protocolo != "https")
                    continue;

                var BindingInfo = Binding.BindingInformation ?? "";
                var Partes = BindingInfo.Split(':');

                if (Partes.Length < 3)
                    continue;

                var Puerto = Partes[1];
                var HostHeader = Partes[2];
                var TieneHostHeader = !string.IsNullOrWhiteSpace(HostHeader);

                var Host = TieneHostHeader ? HostHeader : "localhost";

                var Url = Puerto is "80" or "443"
                    ? $"{Protocolo}://{Host}"
                    : $"{Protocolo}://{Host}:{Puerto}";

                if (TieneHostHeader)
                    UrlsInternas.Add(Url);
                else
                    UrlsLocales.Add(Url);

                var TieneCertificado = Binding.CertificateHash != null
                                       && Binding.CertificateHash.Length > 0;

                if (Protocolo == "https" && TieneCertificado && TieneHostHeader)
                    UrlsExternas.Add(Url);
            }

            if (UrlsLocales.Count == 0 && UrlsInternas.Count == 0)
                continue;

            Resultado.Add(new SitioIIS
            {
                Nombre = Sitio.Name,
                UrlsLocales = UrlsLocales.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                UrlsInternas = UrlsInternas.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                UrlsExternas = UrlsExternas.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        return Resultado;
    }
}
