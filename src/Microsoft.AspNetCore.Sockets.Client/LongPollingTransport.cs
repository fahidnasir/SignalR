// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Sockets.Client.Internal;
using Microsoft.AspNetCore.Sockets.Internal.Formatters;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Sockets.Client
{
    public class LongPollingTransport : ITransport
    {
        private static readonly string DefaultUserAgent = "Microsoft.AspNetCore.SignalR.Client/0.0.0";
        private static readonly ProductInfoHeaderValue DefaultUserAgentHeader = ProductInfoHeaderValue.Parse(DefaultUserAgent);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private IChannelConnection<Message> _application;
        private Task _sender;
        private Task _poller;
        private MessageParser _parser = new MessageParser();

        private readonly CancellationTokenSource _transportCts = new CancellationTokenSource();

        public Task Running { get; private set; }

        public LongPollingTransport(HttpClient httpClient, ILoggerFactory loggerFactory)
        {
            _httpClient = httpClient;
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LongPollingTransport>();
        }

        public Task StartAsync(Uri url, IChannelConnection<Message> application)
        {
            _application = application;

            // Start sending and polling
            _poller = Poll(Utils.AppendPath(url, "poll"), _transportCts.Token);
            _sender = SendMessages(Utils.AppendPath(url, "send"), _transportCts.Token);

            Running = Task.WhenAll(_sender, _poller).ContinueWith(t =>
            {
                _application.Output.TryComplete(t.IsFaulted ? t.Exception.InnerException : null);
                return t;
            }).Unwrap();

            return TaskCache.CompletedTask;
        }

        public async Task StopAsync()
        {
            _transportCts.Cancel();
            try
            {
                await Running;
            }
            catch
            {
                // exceptions have been handled in the Running task continuation by closing the channel with the exception
            }
        }

        private async Task Poll(Uri pollUrl, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                    request.Headers.UserAgent.Add(DefaultUserAgentHeader);

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    if (response.StatusCode == HttpStatusCode.NoContent || cancellationToken.IsCancellationRequested)
                    {
                        // Transport closed or polling stopped, we're done
                        break;
                    }
                    else
                    {
                        // Until Pipeline starts natively supporting BytesReader, this is the easiest way to do this.
                        var payload = await response.Content.ReadAsByteArrayAsync();
                        if (payload.Length > 0)
                        {
                            var reader = new BytesReader(payload);
                            var messageFormat = MessageParser.GetFormat(reader.Unread[0]);
                            reader.Advance(1);

                            _parser.Reset();
                            while (_parser.TryParseMessage(ref reader, messageFormat, out var message))
                            {
                                while (!_application.Output.TryWrite(message))
                                {
                                    if (cancellationToken.IsCancellationRequested || !await _application.Output.WaitToWriteAsync(cancellationToken))
                                    {
                                        return;
                                    }
                                }
                            }

                            // Since we pre-read the whole payload, we know that when this fails we have read everything.
                            // Once Pipelines natively support BytesReader, we could get into situations where the data for
                            // a message just isn't available yet.

                            // If there's still data, we hit an incomplete message
                            if (reader.Unread.Length > 0)
                            {
                                throw new FormatException("Incomplete message");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // transport is being closed
            }
            catch (Exception ex)
            {
                _logger.LogError("Error while polling '{0}': {1}", pollUrl, ex);
                throw;
            }
            finally
            {
                // Make sure the send loop is terminated
                _transportCts.Cancel();
            }
        }

        private async Task SendMessages(Uri sendUrl, CancellationToken cancellationToken)
        {
            try
            {
                while (await _application.Input.WaitToReadAsync(cancellationToken))
                {
                    while (!cancellationToken.IsCancellationRequested && _application.Input.TryRead(out Message message))
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, sendUrl);
                        request.Headers.UserAgent.Add(DefaultUserAgentHeader);

                        if (message.Payload != null && message.Payload.Length > 0)
                        {
                            request.Content = new ByteArrayContent(message.Payload);
                        }

                        var response = await _httpClient.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // transport is being closed
            }
            catch (Exception ex)
            {
                _logger.LogError("Error while sending to '{0}': {1}", sendUrl, ex);
                throw;
            }
            finally
            {
                // Make sure the poll loop is terminated
                _transportCts.Cancel();
            }
        }
    }
}
