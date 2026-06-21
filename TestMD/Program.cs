using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program {
    static async Task Main() {
        var handler = new SocketsHttpHandler() {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var client = new HttpClient(handler);
        client.DefaultRequestVersion = new Version(2, 0);
        client.DefaultRequestHeaders.Add("User-Agent", "Koware/1.0");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        var url = "https://api.mangadex.org/manga?title=Tokyo%20Ghoul&limit=20&offset=0&includes[]=cover_art&order[relevance]=desc&availableTranslatedLanguage[]=en";
        var resp = await client.GetAsync(url);
        Console.WriteLine((int)resp.StatusCode);
        Console.WriteLine(await resp.Content.ReadAsStringAsync());
    }
}
