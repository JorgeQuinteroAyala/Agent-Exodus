using System.Net;
using System.Net.Sockets;

namespace Exodus.Agent.Core.Services;

public static class ResolverDnsPublico
{
    public static async Task<IPAddress> ResolverAsync(string pHost,
        CancellationToken pCancelacionToken)
    {
        try
        {
            using var Udp = new UdpClient();
            Udp.Client.ReceiveTimeout = 3000;

            var Id = (ushort)Random.Shared.Next(0, 65535);
            var Consulta = ConstruirQueryDns(pHost, Id);

            var Servidor = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
            await Udp.SendAsync(Consulta, Consulta.Length, Servidor);

            var Tarea = Udp.ReceiveAsync(pCancelacionToken).AsTask();

            if (await Task.WhenAny(Tarea, Task.Delay(3000, pCancelacionToken)) != Tarea)
                throw new TimeoutException("Timeout al consultar 8.8.8.8");

            var Respuesta = (await Tarea).Buffer;
            var Ip = ParsearRespuestaDNS(Respuesta);

            if (Ip is not null)
                return Ip;
        }
        catch { }

        var Direcciones = await Dns.GetHostAddressesAsync(pHost, pCancelacionToken);
        return Direcciones.First(a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    private static byte[] ConstruirQueryDns(string pHost, ushort pId)
    {
        var MS = new MemoryStream();

        MS.WriteByte((byte)(pId >> 8));
        MS.WriteByte((byte)(pId & 0xFF));
        MS.WriteByte(0x01); MS.WriteByte(0x00);
        MS.WriteByte(0x00); MS.WriteByte(0x01);
        MS.WriteByte(0x00); MS.WriteByte(0x00);
        MS.WriteByte(0x00); MS.WriteByte(0x00);
        MS.WriteByte(0x00); MS.WriteByte(0x00);

        foreach (var Etiqueta in pHost.Split('.'))
        {
            var Bytes = System.Text.Encoding.ASCII.GetBytes(Etiqueta);
            MS.WriteByte((byte)Bytes.Length);
            MS.Write(Bytes);
        }

        MS.WriteByte(0x00);
        MS.WriteByte(0x00); MS.WriteByte(0x01);
        MS.WriteByte(0x00); MS.WriteByte(0x01);

        return MS.ToArray();
    }

    private static IPAddress? ParsearRespuestaDNS(byte[] pRespuesta)
    {
        var Posicion = 12;

        while (Posicion < pRespuesta.Length && pRespuesta[Posicion] != 0)
        {
            if ((pRespuesta[Posicion] & 0xC0) == 0xC0) { Posicion += 2; break; }
            Posicion += pRespuesta[Posicion] + 1;
        }

        if (Posicion < pRespuesta.Length && pRespuesta[Posicion] == 0)
            Posicion++;

        Posicion += 4;

        if (Posicion + 12 > pRespuesta.Length)
            return null;

        if ((pRespuesta[Posicion] & 0xC0) == 0xC0) Posicion += 2;
        else
        {
            while (Posicion < pRespuesta.Length && pRespuesta[Posicion] != 0)
                Posicion += pRespuesta[Posicion] + 1;
            Posicion++;
        }

        var Tipo = (pRespuesta[Posicion] << 8) | pRespuesta[Posicion + 1];
        Posicion += 8;

        var RDLength = (pRespuesta[Posicion] << 8) | pRespuesta[Posicion + 1];
        Posicion += 2;

        if (Tipo == 1 && RDLength == 4)
            return new IPAddress(pRespuesta[Posicion..(Posicion + 4)]);

        return null;
    }
}
