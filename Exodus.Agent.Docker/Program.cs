using Docker.DotNet;
using Exodus.Agent.Core.Configuration;
using Exodus.Agent.Core.Services;
using Exodus.Agent.Docker.Services;
using System.Net;
using System.Net.Sockets;

var Builder = WebApplication.CreateBuilder(args);

Builder.Services.AddControllers()
    .AddApplicationPart(typeof(Exodus.Agent.Core.Controllers.HealthController).Assembly);

// Cliente interno: bypass de validación de certificado (para health internos
// con certs self-signed o expirados que no nos importa validar).
Builder.Services.AddHttpClient("MiClienteApi", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback =
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

// Cliente externo: bypass de cert + resolver DNS contra 8.8.8.8 para
// saltarse el DNS interno del Swarm y probar como lo haría un cliente
// desde internet (usa la IP pública del servicio).
Builder.Services.AddHttpClient("ClienteExterno", c =>
{
c.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => {
        var Handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            },
            ConnectCallback = async (Contexto, CancelacionToken) =>
            {
                var IP = await ResolverDnsPublico.ResolverAsync(
                    Contexto.DnsEndPoint.Host, CancelacionToken);

                var Socket = new Socket(IP.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                await Socket.ConnectAsync(new IPEndPoint(IP, Contexto.DnsEndPoint.Port), CancelacionToken);
                return new NetworkStream(Socket, ownsSocket: true);
            }
        };

        return Handler;
    });

Builder.Services.AgregarExodusCore();

// DockerClient compartido entre VerificadorDocker, ServicioMonitoreoSwarm y SubMuestreadorDocker.
Builder.Services.AddSingleton<DockerClient>(sp =>
{
    var Cfg = sp.GetRequiredService<IConfiguration>();
    var Endpoint = DetectorSistemaOperativo.ObtenerEndpointDocker(Cfg);
    return new DockerClientConfiguration(new Uri(Endpoint)).CreateClient();
});

Builder.Services.AddSingleton<SubMuestreadorDocker>();

Builder.Services.AddSingleton<VerificadorDocker>();
Builder.Services.AddSingleton<IVerificadorPlataforma>(
    sp => sp.GetRequiredService<VerificadorDocker>());

Builder.Services.AddSingleton<ServicioMonitoreoSwarm>();
Builder.Services.AddSingleton<IServicioMonitoreo>(
    sp => sp.GetRequiredService<ServicioMonitoreoSwarm>());
Builder.Services.AddHostedService(sp => sp.GetRequiredService<ServicioMonitoreoSwarm>());

var App = Builder.Build();

using (var Scope = App.Services.CreateScope())
{
    var Inicializador = Scope.ServiceProvider.GetRequiredService<InicializadorIndicesElastic>();
    await Inicializador.AsegurarIndicesAsync(CancellationToken.None);
}

App.MapControllers();
App.Run();
