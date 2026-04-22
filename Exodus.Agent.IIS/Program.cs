using Exodus.Agent.Core.Configuration;
using Exodus.Agent.Core.Services;
using Exodus.Agent.IIS.Services;
using System.Net;
using System.Net.Sockets;

var Builder = WebApplication.CreateBuilder(args);

Builder.Host.UseWindowsService();

Builder.Services.AddControllers().AddApplicationPart(typeof(Exodus.Agent.Core.Controllers.HealthController).Assembly);

Builder.Services.AddHttpClient("ClienteInterno", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

Builder.Services.AddHttpClient("ClienteInterno", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() =>
    {
        var Handler = new SocketsHttpHandler
        {
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

Builder.Services.AddSingleton<SubMuestreadorIIS>();
Builder.Services.AddSingleton<VerificadorIIS>();
Builder.Services.AddSingleton<IVerificadorPlataforma>(sp => sp.GetRequiredService<VerificadorIIS>());

Builder.Services.AddSingleton<DetectorSitiosIIS>();
Builder.Services.AddSingleton<ServicioMonitoreoIIS>();
Builder.Services.AddSingleton<IServicioMonitoreo>(sp => sp.GetRequiredService<ServicioMonitoreoIIS>());
Builder.Services.AddHostedService(sp => sp.GetRequiredService<ServicioMonitoreoIIS>());

var App = Builder.Build();

using (var Scope = App.Services.CreateScope())
{
    var Inicializador = Scope.ServiceProvider.GetRequiredService<InicializadorIndicesElastic>();
    await Inicializador.AsegurarIndicesAsync(CancellationToken.None);
}

App.MapControllers();
App.Run();