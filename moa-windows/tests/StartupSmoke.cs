using System;
using System.IO;
using System.Net;
using System.Threading;
class StartupSmoke
{
    static int Main()
    {
        try { return Run(); }
        catch (Exception ex) { Console.WriteLine("STARTUP FAILED: " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static int Run()
    {
        MoaLocalRuntime.EnsureStartedAsync(CancellationToken.None).GetAwaiter().GetResult();
        var request = (HttpWebRequest)WebRequest.Create(MoaLocalRuntime.BaseUrl + "/v1/status"); request.Proxy = null;
        using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream()))
        {
            string status = reader.ReadToEnd();
            if (!status.Contains("moa-engine") || !status.Contains("\"state\":\"idle\"")) throw new Exception("Expected idle Moa engine.");
        }
        if (!File.Exists(Path.Combine(MoaLocalRuntime.DataDirectory, "native-token.txt"))) throw new Exception("Missing private native token.");
        Console.WriteLine("PASS production client automatically starts idle Moa engine with private token; no model request sent.");
        return 0;
    }
}
