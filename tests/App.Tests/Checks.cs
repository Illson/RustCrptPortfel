using System.Net;
using System.Text;

internal static class Checks
{
    internal static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    internal static void Reject<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(name);
    }
}

internal sealed class MockHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);

    internal static HttpResponseMessage Reply(string data, HttpStatusCode status = HttpStatusCode.OK, string media = "application/json") =>
        new(status) { Content = new StringContent(data, Encoding.UTF8, media) };
}
