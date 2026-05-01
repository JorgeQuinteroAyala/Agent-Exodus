using System.Net;
using System.Net.Sockets;

namespace Exodus.Agent.Core.Services;

public static class ResolverDnsPublico
{
    public static async Task<IPAddress> ResolverAsync(string pHost,
        CancellationToken pCancelacionToken)
    {
        using var Udp = new UdpClient();

        var Id = (ushort)Random.Shared.Next(0, 65535);
        var Consulta = ConstruirQueryDns(pHost, Id);

        var Servidor = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
        await Udp.SendAsync(Consulta, Consulta.Length, Servidor);

        using var Cts = CancellationTokenSource.CreateLinkedTokenSource(pCancelacionToken);
        Cts.CancelAfter(TimeSpan.FromSeconds(3));

        UdpReceiveResult Resultado;
        try
        {
            Resultado = await Udp.ReceiveAsync(Cts.Token);
        }
        catch (OperationCanceledException) when (!pCancelacionToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timeout al resolver '{pHost}' contra 8.8.8.8 (3s).");
        }

        var Ip = ParsearRespuestaDNS(Resultado.Buffer);

        if (Ip is null)
            throw new InvalidOperationException(
                $"8.8.8.8 no devolvió registros A para '{pHost}'.");

        return Ip;
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
        if (pRespuesta.Length < 12) return null;

        // Header DNS: bytes 4-5 = QDCOUNT, bytes 6-7 = ANCOUNT.
        int QdCount = (pRespuesta[4] << 8) | pRespuesta[5];
        int AnCount = (pRespuesta[6] << 8) | pRespuesta[7];
        if (AnCount == 0) return null;

        int Posicion = 12;

        // Saltar la sección de preguntas (QNAME + QTYPE + QCLASS).
        for (int Q = 0; Q < QdCount; Q++)
        {
            Posicion = SaltarNombre(pRespuesta, Posicion);
            if (Posicion < 0) return null;
            Posicion += 4;
        }

        // Iterar Answer records buscando el primer tipo A (TYPE=1, RDLENGTH=4).
        // Los CNAMEs (TYPE=5) y otros tipos se saltan avanzando RDLENGTH bytes.
        for (int A = 0; A < AnCount; A++)
        {
            Posicion = SaltarNombre(pRespuesta, Posicion);
            if (Posicion < 0 || Posicion + 10 > pRespuesta.Length) return null;

            int Tipo = (pRespuesta[Posicion] << 8) | pRespuesta[Posicion + 1];
            int RDLength = (pRespuesta[Posicion + 8] << 8) | pRespuesta[Posicion + 9];
            Posicion += 10;

            if (Posicion + RDLength > pRespuesta.Length) return null;

            if (Tipo == 1 && RDLength == 4)
                return new IPAddress(new[]
                {
                    pRespuesta[Posicion],     pRespuesta[Posicion + 1],
                    pRespuesta[Posicion + 2], pRespuesta[Posicion + 3]
                });

            Posicion += RDLength;
        }

        return null;
    }

    /// <summary>
    /// Avanza el cursor más allá de un nombre DNS (etiquetas length-prefixed
    /// terminadas en 0x00, o un puntero de compresión de 2 bytes con bits altos 0xC0).
    /// Devuelve la nueva posición, o -1 si la respuesta está corrupta.
    /// </summary>
    private static int SaltarNombre(byte[] pRespuesta, int pPosicion)
    {
        while (pPosicion < pRespuesta.Length)
        {
            byte B = pRespuesta[pPosicion];
            if (B == 0) return pPosicion + 1;
            if ((B & 0xC0) == 0xC0) return pPosicion + 2;
            pPosicion += B + 1;
        }
        return -1;
    }
}