﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.IO.Pipelines.Text.Primitives;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Sockets.Internal.Formatters;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Sockets.Transports
{
    public class ServerSentEventsTransport : IHttpTransport
    {
        public static readonly string Name = "serverSentEvents";
        private readonly ReadableChannel<Message> _application;
        private readonly ILogger _logger;

        public ServerSentEventsTransport(ReadableChannel<Message> application, ILoggerFactory loggerFactory)
        {
            _application = application;
            _logger = loggerFactory.CreateLogger<ServerSentEventsTransport>();
        }

        public async Task ProcessRequestAsync(HttpContext context, CancellationToken token)
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Content-Encoding"] = "identity";

            await context.Response.Body.FlushAsync();

            var pipe = context.Response.Body.AsPipelineWriter();
            var output = new PipelineTextOutput(pipe, TextEncoder.Utf8); // We don't need the Encoder, but it's harmless to set.

            try
            {
                while (await _application.WaitToReadAsync(token))
                {
                    var buffer = pipe.Alloc();
                    while (_application.TryRead(out var message))
                    {
                        if (!ServerSentEventsMessageFormatter.TryWriteMessage(message, output))
                        {
                            // We ran out of space to write, even after trying to enlarge.
                            // This should only happen in a significant lack-of-memory scenario.

                            // IOutput doesn't really have a way to write incremental

                            // REVIEW: I really am not sure what exception to throw here... OOM seems right, but it feels so wrong to throw it ourselves...
                            throw new OutOfMemoryException("Ran out of space to format messages!");
                        }

                        // REVIEW: Flushing after each message? Good? Bad? We can't access Commit because it's hidden inside PipelineTextOutput
                        await output.FlushAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Closed connection
            }
        }
    }
}
