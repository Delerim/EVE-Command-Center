using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckCloudBackupOAuthCallback()
    {
        // Exercise the real local HTTP callback; no Google credentials, internet
        // or real OAuth code are used. The old listener.Stop() ordering caused
        // the browser's HTTP request to fail before it received this page.
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();
        string address = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(address);
        listener.Start();
        try
        {
            var callback = CloudBackupService.ReadGoogleCallbackAsync(
                listener, "synthetic-state", CancellationToken.None);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            string html = http.GetStringAsync(
                address + "?state=synthetic-state&code=synthetic-code")
                .GetAwaiter().GetResult();
            string code = callback.GetAwaiter().GetResult();
            Check(code == "synthetic-code" &&
                  html.Contains("Google Drive connected", StringComparison.Ordinal),
                "Google OAuth loopback success page arrives before listener disposal");
        }
        finally
        {
            listener.Stop();
        }
    }
}
