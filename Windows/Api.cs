using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;

namespace OpenCodeTrafficLight
{
    /// <summary>OpenCode 本地 REST API 的只读客户端。</summary>
    internal static class Api
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            // 关闭代理：这些请求只走 127.0.0.1，走系统代理会被拦掉
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.ConnectionClose = false;
            return client;
        }

        public static object Get(ServiceConfig config, string path, int timeoutMs = 2000)
        {
            if (config == null) return null;

            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var request = new HttpRequestMessage(HttpMethod.Get, config.BaseUrl + path))
                {
                    var raw = Encoding.UTF8.GetBytes("opencode:" + config.Password);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                    using (var response = Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                                               .GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        return Json.Parse(body);
                    }
                }
            }
            catch (Exception)
            {
                // 超时 / 服务退出 / 端口变化：调用方按「读不到状态」处理
                return null;
            }
        }
    }
}
