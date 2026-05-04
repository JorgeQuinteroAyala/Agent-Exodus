namespace Exodus.Agent.IIS.Models;

public class SitioIIS
{
    public string Nombre { get; set; } = "";
    public List<string> UrlsLocales { get; set; } = new();
    public List<string> UrlsInternas { get; set; } = new();
    public List<string> UrlsExternas { get; set; } = new();
}
