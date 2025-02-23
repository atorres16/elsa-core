using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Elsa.Activities.Http.Services;
using Newtonsoft.Json.Linq;

namespace Elsa.Activities.Http.Parsers.Response
{
    public class JTokenHttpResponseContentReader : IHttpResponseContentReader
    {
        public string Name => "JToken";
        public int Priority => 0;
        public bool GetSupportsContentType(string contentType) => contentType.Contains("/json", StringComparison.OrdinalIgnoreCase);

        public async Task<object> ReadAsync(SendHttpRequest activity, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var json = (await response.Content.ReadAsStringAsync()).Trim();
            return JToken.Parse(json);
        }
    }
}