// Modifed by SignalFx
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using Datadog.Trace.ExtensionMethods;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Datadog.Trace.TestHelpers
{
    // Modeled from MockTracerAgent
    public class MockZipkinCollector : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;

        public MockZipkinCollector(int port = 9080, int retries = 5)
        {
            // try up to 5 consecutive ports before giving up
            while (true)
            {
                // seems like we can't reuse a listener if it fails to start,
                // so create a new listener each time we retry
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");

                try
                {
                    listener.Start();

                    // successfully listening
                    Port = port;
                    _listener = listener;

                    _listenerThread = new Thread(HandleHttpRequests);
                    _listenerThread.Start();

                    return;
                }
                catch (HttpListenerException) when (retries > 0)
                {
                    // only catch the exception if there are retries left
                    port++;
                    retries--;
                }

                // always close listener if exception is thrown,
                // whether it was caught or not
                listener.Close();
            }
        }

        public event EventHandler<EventArgs<HttpListenerContext>> RequestReceived;

        public event EventHandler<EventArgs<IList<IMockSpan>>> RequestDeserialized;

        /// <summary>
        /// Gets or sets a value indicating whether to skip serialization of traces.
        /// </summary>
        public bool ShouldDeserializeTraces { get; set; } = true;

        /// <summary>
        /// Gets the TCP port that this Agent is listening on.
        /// Can be different from <see cref="MockZipkinCollector(int, int)"/>'s <c>initialPort</c>
        /// parameter if listening on that port fails.
        /// </summary>
        public int Port { get; }

        /// <summary>
        /// Gets the filters used to filter out spans we don't want to look at for a test.
        /// </summary>
        public List<Func<IMockSpan, bool>> SpanFilters { get; private set; } = new List<Func<IMockSpan, bool>>();

        public IImmutableList<IMockSpan> Spans { get; private set; } = ImmutableList<IMockSpan>.Empty;

        public IImmutableList<NameValueCollection> RequestHeaders { get; private set; } = ImmutableList<NameValueCollection>.Empty;

        /// <summary>
        /// Wait for the given number of spans to appear.
        /// </summary>
        /// <param name="count">The expected number of spans.</param>
        /// <param name="timeoutInMilliseconds">The timeout</param>
        /// <param name="operationName">The integration we're testing</param>
        /// <param name="minDateTime">Minimum time to check for spans from</param>
        /// <param name="returnAllOperations">When true, returns every span regardless of operation name</param>
        /// <returns>The list of spans.</returns>
        public IImmutableList<IMockSpan> WaitForSpans(
            int count,
            int timeoutInMilliseconds = 20000,
            string operationName = null,
            DateTimeOffset? minDateTime = null,
            bool returnAllOperations = false)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutInMilliseconds);
            var minimumOffset = (minDateTime ?? DateTimeOffset.MinValue).ToUnixTimeMicroseconds();

            IImmutableList<IMockSpan> relevantSpans = ImmutableList<IMockSpan>.Empty;

            while (DateTime.Now < deadline)
            {
                relevantSpans =
                    Spans
                       .Where(s => SpanFilters.All(shouldReturn => shouldReturn(s)))
                       .Where(s => s.Start > minimumOffset)
                       .ToImmutableList();

                if (relevantSpans.Count(s => operationName == null || s.Name == operationName) >= count)
                {
                    break;
                }

                Thread.Sleep(500);
            }

            if (!returnAllOperations)
            {
                relevantSpans =
                    relevantSpans
                       .Where(s => operationName == null || s.Name == operationName)
                       .ToImmutableList();
            }

            return relevantSpans;
        }

        public void Dispose()
        {
            _listener?.Stop();
        }

        protected virtual void OnRequestReceived(HttpListenerContext context)
        {
            RequestReceived?.Invoke(this, new EventArgs<HttpListenerContext>(context));
        }

        protected virtual void OnRequestDeserialized(IList<IMockSpan> trace)
        {
            RequestDeserialized?.Invoke(this, new EventArgs<IList<IMockSpan>>(trace));
        }

        private void AssertHeader(
            NameValueCollection headers,
            string headerKey,
            Func<string, bool> assertion)
        {
            var header = headers.Get(headerKey);

            if (string.IsNullOrEmpty(header))
            {
                throw new Exception($"Every submission to the agent should have a {headerKey} header.");
            }

            if (!assertion(header))
            {
                throw new Exception($"Failed assertion for {headerKey} on {header}");
            }
        }

        private void HandleHttpRequests()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    OnRequestReceived(ctx);

                    if (ShouldDeserializeTraces)
                    {
                        using (var reader = new StreamReader(ctx.Request.InputStream))
                        {
                            var zspans = JsonConvert.DeserializeObject<List<Span>>(reader.ReadToEnd());
                            IList<IMockSpan> spans = (IList<IMockSpan>)zspans.ConvertAll(x => (IMockSpan)x);
                            OnRequestDeserialized(spans);

                            lock (this)
                            {
                                // we only need to lock when replacing the span collection,
                                // not when reading it because it is immutable
                                Spans = Spans.AddRange(spans);
                                RequestHeaders = RequestHeaders.Add(new NameValueCollection(ctx.Request.Headers));
                            }
                        }
                    }

                    ctx.Response.ContentType = "application/json";
                    var buffer = Encoding.UTF8.GetBytes("{}");
                    ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    ctx.Response.Close();
                }
                catch (HttpListenerException)
                {
                    // listener was stopped,
                    // ignore to let the loop end and the method return
                }
            }
        }

        [DebuggerDisplay("TraceId={TraceId}, SpanId={SpanId}, Service={Service}, Name={Name}, Resource={Resource}")]
        public class Span : IMockSpan
        {
            [JsonExtensionData]
            private IDictionary<string, JToken> _zipkinData;

            public Span()
            {
                _zipkinData = new Dictionary<string, JToken>();
            }

            public ulong TraceId
            {
                get => Convert.ToUInt64(_zipkinData["traceId"].ToString(), 16);
            }

            public ulong SpanId
            {
                get => Convert.ToUInt64(_zipkinData["id"].ToString(), 16);
            }

            public string Name { get; set; }

            public string Resource { get; set; }

            public string Service
            {
                get => _zipkinData["localEndpoint"]["serviceName"].ToString();
            }

            public string Type { get; set; }

            public long Start
            {
                get => Convert.ToInt64(_zipkinData["timestamp"].ToString());
            }

            public long Duration { get; set; }

            public ulong? ParentId
            {
                get => Convert.ToUInt64(_zipkinData["parentId"].ToString(), 16);
            }

            public byte Error { get; set; }

            public Dictionary<string, string> Tags { get; set; }

            public Dictionary<string, double> Metrics { get; set; }

            [OnDeserialized]
            private void OnDeserialized(StreamingContext context)
            {
                var resourceNameTag = (string)DictionaryExtensions.GetValueOrDefault(Tags, "resource.name");
                // If resource.name tag not set, it matches the operation name
                Resource = string.IsNullOrEmpty(resourceNameTag) ? Name : resourceNameTag;
                Type = (string)DictionaryExtensions.GetValueOrDefault(Tags, "span.type");
            }
        }
    }
}