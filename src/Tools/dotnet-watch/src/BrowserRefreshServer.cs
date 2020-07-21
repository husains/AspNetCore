// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Tools
{
    public class BrowserRefreshServer : IDisposable
    {
        private readonly IReporter _reporter;
        private readonly TaskCompletionSource _taskCompletionSource;
        private IHost _refreshServer;
        private WebSocket _webSocket;

        public BrowserRefreshServer(
            IReporter reporter)
        {
            _reporter = reporter;
            _taskCompletionSource = new TaskCompletionSource();
        }

        public string Start()
        {
            _refreshServer = new HostBuilder()
                .ConfigureWebHost(builder =>
                {
                    builder.UseKestrel();
                    builder.UseUrls("http://127.0.0.1:0");

                    builder.Configure(app =>
                    {
                        app.UseWebSockets();
                        app.Run(WebSocketRequest);
                    });
                })
                .Build();

            RunInBackgroundThread(_refreshServer);

            var serverUrl = _refreshServer.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                .Addresses
                .First();

            return serverUrl.Replace("http://", "ws://");
        }

        static void RunInBackgroundThread(IHost host)
        {
            var isDone = new ManualResetEvent(false);

            ExceptionDispatchInfo edi = null;
            Task.Run(() =>
            {
                try
                {
                    host.Start();
                }
                catch (Exception ex)
                {
                    edi = ExceptionDispatchInfo.Capture(ex);
                }

                isDone.Set();
            });

            if (!isDone.WaitOne(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting to start the host");
            }

            if (edi != null)
            {
                throw edi.SourceException;
            }
        }

        async Task WebSocketRequest(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            _webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await _taskCompletionSource.Task;
        }

        public async void SendMessage(byte[] messageBytes)
        {
            if (_webSocket == null || _webSocket.CloseStatus.HasValue)
            {
                return;
            }

            try
            {
                await _webSocket.SendAsync(messageBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _reporter.Output($"Refresh server error: {ex}");
            }
        }

        public void Dispose()
        {
            _refreshServer?.Dispose();
            _taskCompletionSource.TrySetResult();
        }
    }
}
