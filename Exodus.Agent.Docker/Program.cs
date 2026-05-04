using Docker.DotNet;
using Exodus.Agent.Core.Configuration;
using Exodus.Agent.Core.Services;
using Exodus.Agent.Docker.Services;
using System.Net;
using System.Net.Sockets;

var Builder = WebApplication.CreateBuilder(args);

Builder.Services.AddControllers().AddApplicationPart(typeof(Exodus.Agent.Core.Controllers.HealthController).Assembly);

Builder.Services.AddHttpClient("ClienteInterno", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

// Cliente externo: bypass de cert + DNS contra 8.8.8.8 + headers humanos +
// header de bypass del WAF (Akamai) inyectado desde configuración.
Builder.Services.AddHttpClient("ClienteExterno", (sp, c) =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    ConfigurarClienteExterno(c, sp.GetRequiredService<IConfiguration>());
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var Handler = new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        },
        ConnectCallback = async (Contexto, CancelacionToken) =>
        {
            var IP = await ResolverDnsPublico.ResolverAsync(Contexto.DnsEndPoint.Host, CancelacionToken);

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


Builder.Services.AddSingleton<DockerClient>(sp =>
{
    var Cfg = sp.GetRequiredService<IConfiguration>();
    var Endpoint = DetectorSistemaOperativo.ObtenerEndpointDocker(Cfg);

    return new DockerClientConfiguration(new Uri(Endpoint)).CreateClient();
});

Builder.Services.AddSingleton<SubMuestreadorDocker>();
Builder.Services.AddSingleton<VerificadorDocker>();
Builder.Services.AddSingleton<IVerificadorPlataforma>(sp => sp.GetRequiredService<VerificadorDocker>());

Builder.Services.AddSingleton<ServicioMonitoreoSwarm>();
Builder.Services.AddSingleton<IServicioMonitoreo>(sp => sp.GetRequiredService<ServicioMonitoreoSwarm>());
Builder.Services.AddHostedService(sp => sp.GetRequiredService<ServicioMonitoreoSwarm>());

var App = Builder.Build();

using (var Scope = App.Services.CreateScope())
{
    var Inicializador = Scope.ServiceProvider.GetRequiredService<InicializadorIndicesElastic>();

    await Inicializador.AsegurarIndicesAsync(CancellationToken.None);
}

App.MapControllers();
App.Run();

static void ConfigurarClienteExterno(HttpClient pCliente, IConfiguration pConfig)
{
    var H = pCliente.DefaultRequestHeaders;

    H.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    H.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    H.AcceptLanguage.ParseAdd("es-MX,es;q=0.9,en;q=0.8");
    H.AcceptEncoding.ParseAdd("gzip, deflate, br");

    var Nombre = pConfig["Agent:HeaderBypassWaf:Nombre"] ?? "X-ExodusDev";
    var Valor = pConfig["Agent:HeaderBypassWaf:Valor"] ?? "100";

    if (!string.IsNullOrWhiteSpace(Nombre) && !string.IsNullOrWhiteSpace(Valor))
        H.TryAddWithoutValidation(Nombre, Valor);
}